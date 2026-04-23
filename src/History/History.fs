namespace Alma.Status

module History =
    open Microsoft.Extensions.Logging
    open System
    open Alma.Kafka

    open Alma.State.ConcurrentStorage
    open Alma.ServiceIdentification

    open Alma.Status.Common.History

    let private incidentsStorage: State<Service * Instance, IncidentHistory> = State.empty()

    let private minFrom (a: DateTimeOffset) (b: DateTimeOffset) : DateTimeOffset =
        if a < b then a else b

    let private maxTo: (DateTimeOffset option * DateTimeOffset option) -> DateTimeOffset option = function
        | Some a, Some b -> Some (if a > b then a else b)
        | Some a, _ -> Some a
        | _, Some b -> Some b
        | _ -> None

    let private addIncident (logger: ILogger) newIncident =
        let key = Key (newIncident.System, newIncident.Service)
        let knownIncident = incidentsStorage |> State.tryFind key

        let aggregatedIncident =
            match knownIncident with
            | Some knownIncident ->
                let occurrences = knownIncident.Occurrences + 1

                {
                    knownIncident with
                        From = minFrom knownIncident.From newIncident.From
                        Occurrences = occurrences
                        To =
                            match maxTo (knownIncident.To, newIncident.To) with
                            | Some maxTo -> Some maxTo
                            | None when occurrences > 1 -> Some newIncident.From
                            | _ -> None
                }
            | None -> newIncident

        logger.LogDebug (
            "Adding incident: %A (%d)",
            aggregatedIncident.Service |> Instance.service |> Service.concat "-",
            aggregatedIncident.Occurrences
        )

        incidentsStorage
        |> State.set key aggregatedIncident

    let incidents () =
        incidentsStorage
        |> State.values

    let incidentsForService (instance: Instance) =
        incidents ()
        |> List.filter (fun incident -> incident.Service = instance)

    type IncidentEvent = {
        Timestamp: DateTimeOffset
        Instance: Instance
        System: Service
        CheckName: CheckName
        CheckResult: CheckResult
    }

    type ConsumeIncidentEvents = ConnectionConfiguration -> seq<Async<Result<IncidentEvent, ConsumeError>>>

    let private eventToIncidentHistory event =
        let level =
            match event.CheckResult with
            | CheckResult.Warning message -> Some (message, IncidentLevel.Warning)
            | CheckResult.Critical message -> Some (message, IncidentLevel.Critical)
            | _ -> None

        match level with
        | Some (message, level) ->
            Some {
                Service = event.Instance
                System = event.System
                Level = level
                CheckName = event.CheckName |> CheckName.value
                From = event.Timestamp
                Occurrences = 1
                To = None
                Detail =
                    match message with
                    | { Message = None; Note = None } -> None
                    | message ->
                        [
                            message.Message
                            message.Note
                        ]
                        |> List.choose id
                        |> String.concat "\n"
                        |> Some
            }
        | None -> None

    let consumeIncidents (loggerFactory: ILoggerFactory) (consume: ConsumeIncidentEvents) connection =
        let logger = loggerFactory.CreateLogger("History")

        async {
            consume connection
            |> Seq.choose (fun consumeResult ->
                match consumeResult |> Async.RunSynchronously with
                | Ok event -> event |> eventToIncidentHistory
                | Error _ -> None)
            |> Seq.iter (addIncident logger)
        }

    let addIncidentEvents (loggerFactory: ILoggerFactory) events =
        let logger = loggerFactory.CreateLogger("History")

        events |> Seq.choose eventToIncidentHistory |> Seq.iter (addIncident logger)

(* let handler onNewIncident: HttpHandler =
        choose [
            PUT >=> routef "/incident/%s" (fun incident next ctx -> task {
                let! body = ctx.ReadBodyFromRequestAsync()
                addIncident (incident, body)
                do! incidents() |> onNewIncident

                return! text "ok" next ctx
            })

            GET >=> route "/incidents" >=> warbler (fun _ ->
                incidents ()
                |> Seq.collect (fun incident -> incident.Values |> List.map (fun value -> sprintf " - %s: %s" incident.Name value))
                |> String.concat "\n"
                |> text
            )
        ]
    *)
