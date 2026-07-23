namespace ProjectBuild

/// Typed build commands and the RTK-aware runners that execute them.
module internal Commands =
    open System
    open System.IO

    open Fake.Core
    open Fake.IO
    open Fake.IO.FileSystemOperators

    let isRtkActive = Environment.hasEnvironVar "RTK_ACTIVE"

    /// Strips FAKE's planning/progress output when RTK is active; errors pass through.
    [<RequireQualifiedAccess>]
    module CompactTrace =
        let private isNoise (message: string) =
            let m = message.TrimStart ()

            m.StartsWith "Shortened DependencyGraph"
            || m.StartsWith "The running order"
            || m.StartsWith "Group -"
            || m.Contains "<=="
            || m.StartsWith "Building project with version"
            // FAKE's internal command echoes (mono/git probes)
            || m.Contains "(In: "

        let private isSeparator (message: string) =
            let m = message.Trim ()
            m.Length > 0 && m |> Seq.forall ((=) '-')

        // Bare git SHA echoed by `git rev-parse` at startup
        let private isCommitHash (message: string) =
            let m = message.Trim ()
            m.Length = 40 && m |> Seq.forall Uri.IsHexDigit

        let install () =
            if isRtkActive then
                let inner = CoreTracing.defaultConsoleTraceListener
                // the Build Time Report is FAKE's final output, so mute from its header on
                let mutable inReport = false

                let compact =
                    { new ITraceListener with
                        member _.Write data =
                            match data with
                            | TraceData.LogMessage (m, _)
                            | TraceData.TraceMessage (m, _)
                            | TraceData.ImportantMessage m ->
                                if m.Trim () = "Build Time Report" then
                                    inReport <- true

                                if isNoise m || isSeparator m || isCommitHash m || inReport then
                                    ()
                                else
                                    inner.Write data
                            | TraceData.OpenTag (KnownTags.Target name, _) ->
                                Console.ForegroundColor <- ConsoleColor.Green
                                printfn "▸ %s" name
                                Console.ResetColor ()
                            | TraceData.CloseTag (KnownTags.Target _, _, TagStatus.Success) -> ()
                            | _ -> inner.Write data
                    }

                CoreTracing.setTraceListeners [ compact ]

    type DotnetCommand =
        | Build
        | Lint
        | Tests
        | Pack
        | Publish
        | Fable
        | Run
        | WatchRun
        | FableWatch

    type NugetCommand =
        | Push
        | AddSource

    type NpmCommand =
        | Install
        | Version

    /// Arguments are supplied separately at the call site.
    type Command =
        | Dotnet of DotnetCommand
        | Nuget of NugetCommand
        | Npm of NpmCommand
        | Mirrord
        | Raw of exe: string

    [<RequireQualifiedAccess>]
    module Command =
        let private npmPath () =
            match ProcessUtils.tryFindFileOnPath "npm" with
            | Some path -> path
            | None ->
                "npm was not found in path. Please install it and make sure it's available from your path. "
                + "See https://safe-stack.github.io/docs/quickstart/#install-pre-requisites for more info"
                |> failwith

        let render: Command -> string * string list = function
        | Dotnet dotnet ->
            "dotnet",
            match dotnet with
            | Build -> [ "build" ]
            | Lint -> [ "fsharplint"; "lint" ]
            | Tests -> [ "run" ]
            | Pack -> [ "pack" ]
            | Publish -> [ "publish" ]
            | Fable -> [ "fable" ]
            | FableWatch -> [ "fable"; "watch" ]
            | Run -> [ "run" ]
            | WatchRun -> [ "watch"; "run" ]
        | Nuget nuget ->
            "dotnet",
            match nuget with
            | Push -> [ "nuget"; "push" ]
            | AddSource -> [ "nuget"; "add"; "source" ]
        | Npm npm ->
            npmPath (),
            match npm with
            | Install -> [ "install" ]
            | Version -> [ "--version" ]
        | Mirrord -> "mirrord", []
        | Raw exe -> exe, []

    [<RequireQualifiedAccess>]
    module Rtk =
        open RtkFilter

        type Transport =
            /// Generic `rtk err <cmd>`: errors/warnings only.
            | Err
            /// Generic `rtk test <cmd>`: failures only.
            | Test
            /// Direct spawn — no rtk (long-running, or shell-metachar commands).
            | Raw

        let mode: Command -> Transport * Filter option = function
        | Dotnet Build -> Transport.Err, Some (Filter.dotnetBuild (Directory.GetCurrentDirectory ()))
        | Dotnet Lint -> Transport.Raw, Some (Filter.fsharplint (Directory.GetCurrentDirectory ()))
        | Dotnet Tests -> Transport.Test, None
        | Dotnet Pack -> Transport.Err, None
        | Dotnet Fable -> Transport.Err, None
        | Npm Install -> Transport.Err, None
        | Dotnet (Publish | Run | WatchRun | FableWatch) -> Transport.Raw, None
        | Npm Version -> Transport.Raw, None
        | Nuget _ -> Transport.Raw, None
        | Mirrord -> Transport.Raw, None
        | Command.Raw _ -> Transport.Raw, None

        let wrap (transport: Transport) (exe: string) (args: string list): string * string list =
            match transport with
            | Raw -> exe, args
            | Err -> "rtk", "err" :: exe :: args
            | Test -> "rtk", "test" :: exe :: args

    // ---- Runners ----

    let private spawn dir (exe: string) (args: string list) =
        let proc =
            CreateProcess.fromRawCommand exe args |> CreateProcess.withWorkingDirectory dir

        if isRtkActive then
            proc |> CreateProcess.disableTraceCommand
        else
            proc

    /// `(Raw, None)` when RTK is inactive, so inactive output is byte-identical.
    let private plan (command: Command) =
        if isRtkActive then
            Rtk.mode command
        else
            Rtk.Transport.Raw, None

    let private wrapped (command: Command) (args: string list) (dir: string) =
        let transport, _ = plan command
        let exe, verb = Command.render command
        let exe', args' = Rtk.wrap transport exe (verb @ args)
        spawn dir exe' args'

    /// The post-filter is applied by `run`, not here — safe for parallel use.
    let toProcess (command: Command) (args: string list) (dir: string) =
        wrapped command args dir |> CreateProcess.ensureExitCode

    // ---- Full-output tee ----

    let private teeDir = Path.Combine (Path.GetTempPath (), "fake-rtk-tee")

    /// Keep only the newest `keep` logs; the epoch-prefixed names sort chronological.
    let private rotateTee keep =
        let logs = Directory.GetFiles (teeDir, "*.log") |> Array.sort
        let excess = logs.Length - keep

        if excess > 0 then
            logs[.. excess - 1] |> Array.iter File.delete

    /// Save full stdout to a rotated temp log, returning a `[full output: <path>]` hint; best-effort.
    let private teeFullOutput (command: Command) (output: string): string option =
        try
            Directory.ensure teeDir
            let slug = Command.render command |> fst
            let stamp = DateTimeOffset.Now.ToUnixTimeSeconds ()
            let unique = Path.GetRandomFileName().Substring (0, 8)
            let path = teeDir </> sprintf "%d_%s_%s.log" stamp slug unique
            File.writeString false path output
            rotateTee 20
            Some (sprintf "[full output: %s]" path)
        with _ ->
            None

    let private exitCode (command: Command) (args: string list) (dir: string): int =
        match plan command with
        | _, Some filter ->
            let result = wrapped command args dir |> CreateProcess.redirectOutput |> Proc.run
            let raw = result.Result.Output
            let filtered = RtkFilter.runBuffered filter raw result.ExitCode

            filtered |> List.iter (printfn "%s")

            let meaningful lines =
                lines |> Seq.filter (fun (l: string) -> l.Trim () <> "") |> Seq.length

            if result.ExitCode <> 0 && meaningful (raw.Split '\n') > meaningful filtered then
                teeFullOutput command raw |> Option.iter (printfn "%s")

            eprintf "%s" result.Result.Error
            result.ExitCode
        | _, None -> (wrapped command args dir |> Proc.run).ExitCode

    /// Raises on a non-zero exit code.
    let run (command: Command) (args: string list) (dir: string) =
        match exitCode command args dir with
        | 0 -> ()
        | code ->
            let exe, verb = Command.render command
            failwithf "`%s` exited with code %d" (exe :: verb |> String.concat " ") code

    let runInRoot (command: Command) (args: string list) = run command args "."
