open Alma.Build
open Fake.Core
open Fake.IO.FileSystemOperators
open Fake.IO.Globbing.Operators

open Utils

[<EntryPoint>]
let main args =
    args |> Args.init

    Targets.init {
        Project = {
            Name = "Alma.Status"
            Summary =
                "Library for running health checks for services and data objects, aggregating status output, and collecting incident history."
            Git = Git.init ()
        }
        Specs =
            Spec.defaultLibrary
            |> Spec.mapLibrary (
                fun library -> {
                    library with
                        NugetApi = NugetApi.KeyInEnvironment "NUGET_API_KEY"
                }
            )
    }

    args |> Args.run
