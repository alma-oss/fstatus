namespace ProjectBuild

/// Buffered output filters for RTK-wrapped build commands.
module internal RtkFilter =
    open System.Text.RegularExpressions

    type Filter = { Run: string list -> int -> string list }

    [<RequireQualifiedAccess>]
    module Filter =
        let create (run: string list -> int -> string list): Filter = { Run = run }

        let ofLines (f: string list -> string list): Filter = create (fun lines _ -> f lines)

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
                repoRoot.TrimEnd (System.IO.Path.DirectorySeparatorChar)
                + string System.IO.Path.DirectorySeparatorChar,
                ""
            )

        /// `rtk err dotnet build` forwards MSBuild diagnostics amid restore/progress
        /// chatter, repeating each once per restore/build phase; keep the deduped
        /// non-NuGet diagnostics, with locations relative to `repoRoot`.
        let dotnetBuild (repoRoot: string): Filter =
            ofLines (
                List.filter (fun line -> isDiagnostic line && not (isNugetNoise line))
                >> List.map (relativize repoRoot)
                >> List.distinct
            )
            |> okWhenEmpty "dotnet build: ok"

        /// Parses `dotnet fsharplint lint` output into one compact diagnostic per warning. The tool emits
        /// per-file blocks (`========== Linting <path> ==========` … `========== Finished: N ==========`);
        /// inside a block each warning is `<message>` / `Error on line L starting at column C` / source /
        /// caret / `See …/FL####.html`, warnings separated by a `----` rule.
        module private Fsharplint =
            let private lintingBanner = Regex @"^========== Linting (.+?) =========="
            let private errorLocation = Regex @"^Error on line (\d+) starting at column (\d+)"
            let private ruleUrl = Regex @"(FL\d+)\.html"

            let private isBoundary (line: string) =
                line.StartsWith "=========="
                || (line.Length > 0 && line |> Seq.forall ((=) '-'))

            type File = File of string

            [<RequireQualifiedAccess>]
            module File =
                let value (File file) = file

            type Line = Line of string

            [<RequireQualifiedAccess>]
            module Line =
                let value (Line line) = line

            type Column = Column of string

            [<RequireQualifiedAccess>]
            module Column =
                let value (Column column) = column

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
                Rule: Rule
                Message: Message
            }

            let format (w: Warning) =
                sprintf
                    "  %s:%s:%s %s  %s"
                    (File.value w.File)
                    (Line.value w.Line)
                    (Column.value w.Column)
                    (Rule.value w.Rule)
                    (Message.value w.Message)

            let private byFile repoRoot lines =
                lines
                |> List.scan
                    (fun file line ->
                        let banner = lintingBanner.Match line

                        if banner.Success then
                            System.IO.Path.GetRelativePath (repoRoot, banner.Groups[1].Value)
                        else
                            file
                    )
                    ""
                |> List.tail
                |> List.zip lines

            let private windows located =
                List.foldBack
                    (fun (line: string, file) acc ->
                        if isBoundary line then
                            [] :: acc
                        else
                            ((line, file) :: List.head acc) :: List.tail acc
                    )
                    located
                    [ [] ]
                |> List.filter (not << List.isEmpty)

            let private ofWindow (window: (string * string) list): Warning option =
                let lines = window |> List.map fst

                lines
                |> List.tryPick (fun l ->
                    let m = errorLocation.Match l
                    if m.Success then Some m else None
                )
                |> Option.map (fun location ->
                    let rule =
                        lines
                        |> List.tryPick (fun l ->
                            let m = ruleUrl.Match l
                            if m.Success then Some m.Groups[1].Value else None
                        )
                        |> Option.defaultValue "FL"

                    let message =
                        lines
                        |> List.map (fun l -> l.Trim ())
                        |> List.tryFind ((<>) "")
                        |> Option.defaultValue ""

                    {
                        File = File (window |> List.head |> snd)
                        Line = Line location.Groups[1].Value
                        Column = Column location.Groups[2].Value
                        Rule = Rule rule
                        Message = Message message
                    }
                )

            let parse repoRoot: string list -> Warning list =
                byFile repoRoot >> windows >> List.choose ofWindow

            let header (warnings: Warning list) =
                let count = List.length warnings
                sprintf "fsharplint: %d %s" count (if count = 1 then "warning" else "warnings")

        let private fsharplintSummary (repoRoot: string): Filter =
            ofLines (fun lines ->
                match Fsharplint.parse repoRoot lines with
                | [] -> []
                | warnings -> Fsharplint.header warnings :: List.map Fsharplint.format warnings
            )

        let fsharplint (repoRoot: string): Filter =
            fsharplintSummary repoRoot |> okWhenEmpty "fsharplint: ok"

    let runBuffered (filter: Filter) (captured: string) (exitCode: int): string list =
        let lines =
            captured.Split '\n' |> Array.toList |> List.map (fun line -> line.TrimEnd '\r')

        filter.Run lines exitCode
