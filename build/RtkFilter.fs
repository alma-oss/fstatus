namespace ProjectBuild

/// Buffered output filters for RTK-wrapped build commands.
module internal RtkFilter =
    open System.IO
    open System.Text.RegularExpressions

    type Filter = { Run: string list -> int -> string list }

    [<RequireQualifiedAccess>]
    module Filter =
        let create (run: string list -> int -> string list): Filter = { Run = run }

        let ofLines (f: string list -> string list): Filter = create (fun lines _ -> f lines)

        let passthrough: Filter = ofLines id

        /// When `inner` keeps nothing, a clean exit collapses to `message`; a failing one shows raw output.
        let okWhenEmpty (message: string) (inner: Filter): Filter =
            create (fun lines exitCode ->
                match inner.Run lines exitCode with
                | [] -> if exitCode = 0 then [ message ] else lines
                | kept -> kept
            )

        /// NuGet audit/prune warnings; NU *errors* are not noise.
        let private isNugetNoise (line: string) =
            Regex.IsMatch (line, @"NU\d{4}") && not (line.Contains "error")

        let private isDiagnostic (line: string) =
            Regex.IsMatch (line, @"(warning|error) [A-Z]+\d+")

        /// MSBuild prints absolute paths both as the diagnostic location and in the
        /// trailing `[…fsproj]`; strip the repo-root prefix from each occurrence.
        let private relativize (repoRoot: string) (line: string) =
            line.Replace (
                repoRoot.TrimEnd (Path.DirectorySeparatorChar)
                + string Path.DirectorySeparatorChar,
                ""
            )

        /// MSBuild emits its diagnostics amid restore/progress chatter, repeating each once per
        /// restore/build phase; keep the deduped non-NuGet ones, with locations relative to
        /// `repoRoot`. `label` names the command in the all-clear line.
        let private msbuild (label: string) (repoRoot: string): Filter =
            ofLines (
                List.filter (fun line -> isDiagnostic line && not (isNugetNoise line))
                >> List.map (relativize repoRoot)
                >> List.distinct
            )
            |> okWhenEmpty (sprintf "%s: ok" label)

        let dotnetBuild: string -> Filter = msbuild "dotnet build"

        let dotnetPublish: string -> Filter = msbuild "dotnet publish"

        /// Parses `dotnet fsharplint lint` output into one compact diagnostic per warning.
        /// The tool emits one block per linted file, warnings within a block separated by a
        /// `----` rule and the whole run closed by a `Summary:` banner:
        ///
        ///     ========== Linting /abs/repo/tests/Tests.fs ==========
        ///     ========== Finished: 0 warnings ==========
        ///     ========== Linting /abs/repo/tests/HealthCheckUrl.fs ==========
        ///     Found trailing whitespace at end of line.
        ///     Error on line 19 starting at column 33
        ///             Instance.createFromValues
        ///                                      ^
        ///     See https://fsprojects.github.io/FSharpLint/how-tos/rules/FL0061.html
        ///     ----------------------------------------------------------------
        ///     ========== Finished: 1 warnings ==========
        ///     ========== Summary: 1 warnings ==========
        ///
        /// giving `  tests/HealthCheckUrl.fs:19:33 FL0061  Found trailing whitespace at end of line.`
        module private Fsharplint =
            let private lintingBanner = Regex @"^========== Linting (.+?) =========="
            let private errorLocation = Regex @"^Error on line (\d+) starting at column (\d+)"
            let private ruleUrl = Regex @"(FL\d+)\.html"
            let private summaryBanner = Regex @"^========== Summary: (\d+) warnings? =========="

            /// One line of the tool's output, newline already stripped.
            type OutputLine = OutputLine of string

            [<RequireQualifiedAccess>]
            module OutputLine =
                let value (OutputLine line) = line

                /// The tool's separator is a full-width rule; demanding that width keeps a source
                /// line of its own dashes, echoed as a warning's context, from splitting the window.
                let private separatorWidth = 80

                /// Banner rules (`==========`) and warning separators (`----`) both close a window.
                let isBoundary (OutputLine line) =
                    line.StartsWith "=========="
                    || (line.Length >= separatorWidth && line |> Seq.forall ((=) '-'))

                let tryMatch (regex: Regex) (OutputLine line) =
                    let matched = regex.Match line
                    if matched.Success then Some matched else None

            /// Path relative to the repo root — the tool itself prints absolute ones.
            type File = File of string

            [<RequireQualifiedAccess>]
            module File =
                let value (File file) = file

            /// 1-based source line, as the tool prints it.
            type Line = Line of string

            [<RequireQualifiedAccess>]
            module Line =
                let value (Line line) = line

            /// 0-based source column, as the tool prints it — the offset its caret sits under,
            /// one less than the column an editor shows.
            type Column = Column of string

            [<RequireQualifiedAccess>]
            module Column =
                let value (Column column) = column

            /// Rule code such as `FL0061`.
            type Rule = Rule of string

            [<RequireQualifiedAccess>]
            module Rule =
                let value (Rule rule) = rule

            type Message = Message of string

            [<RequireQualifiedAccess>]
            module Message =
                let value (Message message) = message

            type Warning = {
                File: File
                Line: Line
                Column: Column
                Rule: Rule option
                Message: Message
            }

            /// `  tests/HealthCheckUrl.fs:19:33 FL0061  Found trailing whitespace at end of line.`,
            /// the code left out entirely when the tool named no rule.
            let format (w: Warning) =
                let rule =
                    w.Rule |> Option.map (Rule.value >> sprintf " %s") |> Option.defaultValue ""

                sprintf
                    "  %s:%s:%s%s  %s"
                    (File.value w.File)
                    (Line.value w.Line)
                    (Column.value w.Column)
                    rule
                    (Message.value w.Message)

            /// One `Linting` banner and every line below it up to the next one — so `Body` also
            /// holds the block's own `Finished:` banner, and the last section holds `Summary:`.
            type Section = { File: File; Body: OutputLine list }

            /// A maximal run of non-boundary lines within a section, tagged with that section's
            /// file — for a single warning, its message / location / source / caret / URL lines.
            type Window = { File: File; Lines: OutputLine list }

            [<RequireQualifiedAccess>]
            module Section =
                /// Splits on `Linting` banners, resolving each announced path against `repoRoot`.
                /// A banner closes the run of lines below it, so lines gathered after the last
                /// banner are seen first and belong to no file — dropped by taking `snd`.
                let listOf repoRoot (lines: OutputLine list): Section list =
                    (lines, ([], []))
                    ||> List.foldBack (fun line (body, sections) ->
                        match OutputLine.tryMatch lintingBanner line with
                        | Some banner ->
                            let file = Path.GetRelativePath (repoRoot, banner.Groups[1].Value)
                            [], { File = File file; Body = body } :: sections
                        | None -> line :: body, sections
                    )
                    |> snd

                /// The boundary lines that separate windows are dropped along with the empty runs
                /// between adjacent ones, so a section for a clean file yields no windows at all.
                let windows (section: Section) =
                    (section.Body, [])
                    ||> List.foldBack (fun line windows ->
                        match OutputLine.isBoundary line, windows with
                        | true, _ -> [] :: windows
                        | false, current :: rest -> (line :: current) :: rest
                        | false, [] -> [ [ line ] ]
                    )
                    |> List.filter (not << List.isEmpty)
                    |> List.map (fun lines -> { File = section.File; Lines = lines })

            [<RequireQualifiedAccess>]
            module Window =
                let private firstGroupOf regex window =
                    window.Lines
                    |> List.tryPick (OutputLine.tryMatch regex)
                    |> Option.map (fun matched -> matched.Groups[1].Value)

                /// `None` unless the window carries an `Error on line …` line, which is what tells a
                /// warning apart from the tool's own chatter. `Message` is the window's first
                /// non-blank line; `Rule` is absent when the window printed no rule URL.
                let toWarning (window: Window): Warning option =
                    window.Lines
                    |> List.tryPick (OutputLine.tryMatch errorLocation)
                    |> Option.map (fun location ->
                        let message =
                            window.Lines
                            |> List.map (OutputLine.value >> _.Trim())
                            |> List.tryFind ((<>) "")
                            |> Option.defaultValue ""

                        {
                            File = window.File
                            Line = Line location.Groups[1].Value
                            Column = Column location.Groups[2].Value
                            Rule = window |> firstGroupOf ruleUrl |> Option.map Rule
                            Message = Message message
                        }
                    )

            let parse repoRoot: string list -> Warning list =
                List.map OutputLine
                >> Section.listOf repoRoot
                >> List.collect Section.windows
                >> List.choose Window.toWarning

            /// The tally the tool prints for itself, to check a parse against. `None` when the
            /// run emitted no summary at all, which is itself a reason not to trust the parse.
            let reportedCount: string list -> int option =
                List.map OutputLine
                >> List.tryPick (OutputLine.tryMatch summaryBanner)
                >> Option.bind (fun matched ->
                    match System.Int32.TryParse matched.Groups[1].Value with
                    | true, count -> Some count
                    | false, _ -> None
                )

            /// `fsharplint: 1 warning`
            let header (warnings: Warning list) =
                let count = List.length warnings
                sprintf "fsharplint: %d %s" count (if count = 1 then "warning" else "warnings")

            /// `fsharplint: parsed 3 of 6 warnings — output format changed, showing it raw`
            let driftNotice (parsed: int) (reported: int) =
                sprintf "fsharplint: parsed %d of %d warnings — output format changed, showing it raw" parsed reported

        /// A parse that loses warnings would silently under-report, since this summary is all
        /// the caller sees; disagreeing with the tool's own tally hands back its raw output.
        let private fsharplintSummary (repoRoot: string): Filter =
            ofLines (fun lines ->
                match Fsharplint.parse repoRoot lines, Fsharplint.reportedCount lines with
                | warnings, Some reported when reported <> List.length warnings ->
                    Fsharplint.driftNotice (List.length warnings) reported :: lines
                | [], _ -> []
                | warnings, _ -> Fsharplint.header warnings :: List.map Fsharplint.format warnings
            )

        let fsharplint (repoRoot: string): Filter =
            fsharplintSummary repoRoot |> okWhenEmpty "fsharplint: ok"

    /// Splitting a newline-terminated output yields an empty final line; dropping it here keeps
    /// every filter — and the caller, which prints one newline per line — from seeing a blank one.
    let private toLines (captured: string) =
        captured.Split '\n'
        |> Array.toList
        |> List.map (fun line -> line.TrimEnd '\r')
        |> List.rev
        |> function
            | "" :: rest -> rest
            | lines -> lines
        |> List.rev

    let runBuffered (filter: Filter) (captured: string) (exitCode: int): string list =
        filter.Run (toLines captured) exitCode
