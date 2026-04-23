namespace Alma.Status

open Microsoft.Extensions.Logging
open System.IO
open Alma.ServiceIdentification
open Feather.ErrorHandling

module IncidentManagement =
    open Alma.Kafka
    open Alma.EnvironmentModel

    type ProduceEvent = MessageToProduce -> unit

    type CreateIncidentMessage<'Application> =
        'Application -> Environment -> Service -> Instance -> CheckName -> CheckResult -> MessageToProduce option

    type ParseIncidentEvent = string -> Result<History.IncidentEvent, string>

    module Event =
        type KeyData = {
            Instance: Instance
            Environment: Environment
            CheckResult: CheckResult
        }

        type DomainData = {
            System: Service
            CheckName: CheckName
            CheckMessage: CheckMessage
        }

    let produceIncidentEvent
        createIncidentMessage
        currentApplication
        (produce: ProduceEvent)
        environment
        system
        service
        check
        =
        check
        |> Check.map (fun checkResult ->
            createIncidentMessage currentApplication environment system service check.Name checkResult
            |> Option.iter produce

            checkResult
        )

    let loadIncidentsFromFile (loggerFactory: ILoggerFactory) parseIncidentEvent file =
        async {
            let! content = file |> File.ReadAllLinesAsync |> Async.AwaitTask

            let incidents = content |> Seq.choose (parseIncidentEvent >> Result.toOption)

            incidents |> History.addIncidentEvents loggerFactory
        }
