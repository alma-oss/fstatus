namespace ProjectBuild

/// Typed build commands and the RTK-aware runners that execute them: FAKE trace compaction,
/// the command vocabulary, RTK transport selection, serial and parallel runners, and the
/// temp-log tee that keeps a failing run's suppressed output reachable.
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
        | Raw of cmd: string

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
                "nuget" ::
                    match nuget with
                    | Push -> [ "push" ]
                    | AddSource -> [ "add"; "source" ]
            | Npm npm ->
                npmPath (),
                match npm with
                | Install -> [ "install" ]
                | Version -> [ "--version" ]
            | Mirrord -> "mirrord", []
            | Raw cmd -> cmd, []

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
            | Dotnet Publish -> Transport.Raw, Some (Filter.dotnetPublish (Directory.GetCurrentDirectory ()))
            | Dotnet Lint -> Transport.Raw, Some (Filter.fsharplint (Directory.GetCurrentDirectory ()))
            | Dotnet Tests -> Transport.Test, None
            | Dotnet Pack -> Transport.Err, None
            | Dotnet Fable -> Transport.Err, None
            | Npm Install -> Transport.Err, None
            | Dotnet (Run | WatchRun | FableWatch) -> Transport.Raw, None
            | Npm Version -> Transport.Raw, None
            | Nuget _ -> Transport.Raw, None
            | Mirrord -> Transport.Raw, None
            | Command.Raw _ -> Transport.Raw, None

        let wrap (transport: Transport) (cmd: string) (args: string list): string * string list =
            match transport with
            | Raw -> cmd, args
            | Err -> "rtk", "err" :: cmd :: args
            | Test -> "rtk", "test" :: cmd :: args

    /// Label the parallel runner prefixes a job's output lines with.
    type JobName = JobName of string

    [<RequireQualifiedAccess>]
    module JobName =
        let value (JobName name) = name

    type ExitCode = ExitCode of int

    [<RequireQualifiedAccess>]
    module ExitCode =
        let value (ExitCode code) = code
        let isSuccess (ExitCode code) = code = 0

    /// Everything a process wrote to stdout, newlines included.
    type CapturedOutput = CapturedOutput of string

    [<RequireQualifiedAccess>]
    module CapturedOutput =
        let value (CapturedOutput output) = output

    /// How the parallel runner surfaces a job's output.
    type OutputMode =
        /// Lines appear as the process writes them — the only option for one that never exits.
        | Streamed
        /// Captured, then rendered into the lines to print once the process has exited.
        | Buffered of (CapturedOutput -> ExitCode -> string list)

    type ParallelJob = {
        Name: JobName
        Process: CreateProcess<ProcessResult<unit>>
        Output: OutputMode
    }

    // ---- Runners ----

    let private spawn dir (cmd: string) (args: string list) =
        let proc =
            CreateProcess.fromRawCommand cmd args |> CreateProcess.withWorkingDirectory dir

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
        let cmd, verb = Command.render command
        let cmd', args' = Rtk.wrap transport cmd (verb @ args)
        spawn dir cmd' args'

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

    /// Lines to print for a finished buffered run: the filtered output, plus a `[full output: …]`
    /// hint when a failing run had output suppressed — the filtered lines are all the caller sees.
    let private report (command: Command) (filter: RtkFilter.Filter) (CapturedOutput raw) (ExitCode code): string list =
        let filtered = RtkFilter.runBuffered filter raw code

        let meaningful lines =
            lines |> Seq.filter (fun (l: string) -> l.Trim () <> "") |> Seq.length

        if code <> 0 && meaningful (raw.Split '\n') > meaningful filtered then
            filtered @ (teeFullOutput command raw |> Option.toList)
        else
            filtered

    /// Exit-code handling belongs to the parallel runner, which prints a buffered job's report
    /// before failing the build, so the process carries no `ensureExitCode`.
    let toJob (name: JobName) (command: Command) (args: string list) (dir: string): ParallelJob = {
        Name = name
        Process = wrapped command args dir
        Output =
            match plan command |> snd with
            | Some filter -> Buffered (report command filter)
            | None -> Streamed
    }

    /// rtk closes a clean run with an `[ok] …` line carrying no newline, so even an unfiltered
    /// rtk run has to be captured and reprinted; a direct spawn keeps the console it inherited.
    let private outputFilter: Rtk.Transport * RtkFilter.Filter option -> RtkFilter.Filter option = function
        | Rtk.Transport.Raw, None -> None
        | _, None -> Some RtkFilter.Filter.passthrough
        | _, filter -> filter

    let private exitCode (command: Command) (args: string list) (dir: string): int =
        match plan command |> outputFilter with
        | Some filter ->
            let result = wrapped command args dir |> CreateProcess.redirectOutput |> Proc.run

            report command filter (CapturedOutput result.Result.Output) (ExitCode result.ExitCode)
            |> List.iter (printfn "%s")

            eprintf "%s" result.Result.Error
            result.ExitCode
        | None -> (wrapped command args dir |> Proc.run).ExitCode

    /// Raises on a non-zero exit code.
    let run (command: Command) (args: string list) (dir: string) =
        match exitCode command args dir with
        | 0 -> ()
        | code ->
            let cmd, verb = Command.render command
            failwithf "`%s` exited with code %d" (cmd :: verb |> String.concat " ") code

    let runInRoot (command: Command) (args: string list) = run command args "."

    // ---- Parallel runner ----

    module private Parallel =
        let locker = obj ()

        let colors = [|
            ConsoleColor.Blue
            ConsoleColor.Yellow
            ConsoleColor.Magenta
            ConsoleColor.Cyan
            ConsoleColor.DarkBlue
            ConsoleColor.DarkYellow
            ConsoleColor.DarkMagenta
            ConsoleColor.DarkCyan
        |]

        let print color (colored: string) (line: string) =
            lock locker (fun () ->
                let currentColor = Console.ForegroundColor
                Console.ForegroundColor <- color
                Console.Write colored
                Console.ForegroundColor <- currentColor
                Console.WriteLine line
            )

        let onStdout index (JobName name) (line: string) =
            let color = colors[index % colors.Length]

            if isNull line then
                print color $"{name}: --- END ---" ""
            else if String.isNotNullOrEmpty line then
                print color $"{name}: " line

        let onStderr (JobName name) (line: string) =
            let color = ConsoleColor.Red

            if isNull line |> not then
                print color $"{name}: " line

        let printStarting indexed =
            for (index, job) in indexed do
                let color = colors[index % colors.Length]
                let name = job.Name |> JobName.value
                let wd = job.Process.WorkingDirectory |> Option.defaultValue ""
                let exe = job.Process.Command.Executable
                let args = job.Process.Command.Arguments.ToStartInfo
                print color $"{name}: {wd}> {exe} {args}" ""

        let private runStreaming index (job: ParallelJob) =
            job.Process
            |> CreateProcess.redirectOutputIfNotRedirected
            |> CreateProcess.withOutputEvents (onStdout index job.Name) (onStderr job.Name)
            |> Proc.run
            |> _.ExitCode
            |> ExitCode

        /// A buffered job yields nothing until it exits, so its whole report is printed in one
        /// locked pass — otherwise a concurrent job's live lines would split the block.
        let private runBuffered index (job: ParallelJob) report =
            let color = colors[index % colors.Length]
            let name = job.Name |> JobName.value
            let result = job.Process |> CreateProcess.redirectOutput |> Proc.run
            let code = ExitCode result.ExitCode

            lock locker (fun () ->
                for line in report (CapturedOutput result.Result.Output) code do
                    print color $"{name}: " line
            )

            eprintf "%s" result.Result.Error
            code

        let private restoreTerminalState () =
            // Reset basic TTY state in case an interrupted child process leaves it broken.
            // Without a terminal on stdin there is nothing to reset and `stty` only errors,
            // which would land in the middle of a buffered job's block.
            if not Console.IsInputRedirected then
                try
                    use procHandle =
                        Diagnostics.Process.Start(
                            Diagnostics.ProcessStartInfo(
                                FileName = "stty",
                                Arguments = "sane",
                                UseShellExecute = false,
                                RedirectStandardOutput = true,
                                RedirectStandardError = true
                            )
                        )

                    procHandle.WaitForExit(1000) |> ignore
                with _ ->
                    ()

        let run jobs =
            try
                jobs
                |> Seq.toArray
                |> Array.indexed
                |> fun x ->
                    printStarting x
                    x
                |> Array.Parallel.map (fun (index, job) ->
                    let code =
                        match job.Output with
                        | Streamed -> runStreaming index job
                        | Buffered report -> runBuffered index job report

                    job.Name, code
                )
            finally
                restoreTerminalState ()

    /// Raises when any job exits non-zero, after every job has finished and reported.
    let runParallel jobs =
        match jobs |> Parallel.run |> Array.filter (snd >> ExitCode.isSuccess >> not) with
        | [||] -> ()
        | failed ->
            failed
            |> Array.map (fun (name, code) -> sprintf "%s (exit %d)" (JobName.value name) (ExitCode.value code))
            |> String.concat ", "
            |> failwithf "Parallel run failed: %s"
