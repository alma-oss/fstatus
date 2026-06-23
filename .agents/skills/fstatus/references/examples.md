# Examples — Alma.Status

This file is the single source of truth for all code in this skill. Examples are
ordered from basic to a full workflow and are self-contained.

## Basic Custom Check

A hand-rolled `Check` returning a `CheckResult`, using the `CheckResult` helpers.

```fsharp
open Alma.Status
open Alma.ServiceIdentification

// Replace pingService with the host's real call to the dependency.
let dependencyCheck (pingService: unit -> Async<Result<int, string>>) (instance: Instance) : Check = {
    Name = CheckName.ofInstance "Dependency" instance
    Execute = async {
        match! pingService () with
        | Ok latencyMs when latencyMs < 200 -> return CheckResult.success
        | Ok latencyMs -> return CheckResult.warning (sprintf "Slow response: %dms" latencyMs)
        | Error error -> return CheckResult.critical error
    }
}
```

## Severity Composition

Reinterpret a check's result after execution by piping it through a `Severity`.

```fsharp
open System
open Alma.Status

// Downgrade Critical -> Warning for a non-essential component.
let lenient (check: Check) =
    check |> Severity.notCritical

// Stay at Warning until the check has failed continuously for 5 minutes.
let patient (check: Check) =
    check |> Severity.criticalAfterTime (TimeSpan.FromMinutes 5.0)
```

## Realistic System

Assemble services and data objects, built from `HealthCheck` constructors, into a
`SoftwareSystem`. Uses neutral placeholder names only.

```fsharp
open System
open Microsoft.Extensions.Logging
open Alma.Status
open Alma.Status.HealthCheck
open Alma.ServiceIdentification

// Replace with however the host constructs Instance values.
let getInstance (name: string) : Instance =
    Instance.parse "-" name
    |> Option.defaultWith (fun () -> failwith "Invalid instance")

let webApiInstance = getInstance "demo-webapi"
let cacheInstance = getInstance "demo-cache"

let buildSystem (loggerFactory: ILoggerFactory) : SoftwareSystem =
    let webApiChecks =
        [ healthCheck loggerFactory [] "HEAD" webApiInstance
          healthCheckSecuredResource loggerFactory "GET" webApiInstance
            |> Severity.criticalAfterTime (TimeSpan.FromMinutes 5.0) ]

    let cacheMetricUrls = k8sSTSPodsAtPort 9121 3 cacheInstance "/metrics"
    let cacheChecks =
        [ healthCheckMetricAboveZero loggerFactory "connected_replicas" cacheMetricUrls cacheInstance
            |> Severity.notCritical ]

    {
        Name = SystemName "DemoSystem"
        Service = { Domain = Domain "example"; Context = Context "demo" }
        Tags = [ Tag "platform" ]
        Services =
            [ { Name = "WebApi"
                Instance = webApiInstance
                Tags = [ Tag "eks"; Tag "public" ]
                StatusPage = K8sInternalService "/status"
                Checks = webApiChecks } ]
        DataObjects =
            [ { Name = "CacheInstance"
                Instance = cacheInstance
                Tags = [ Tag "internal" ]
                Checks = cacheChecks
                Details = [] } ]
    }
```

## Register And Run

Register systems, collect the returned polling workflows, and read state. The host
is responsible for running the returned `Async<unit>` workflows.

```fsharp
open Microsoft.Extensions.Logging
open Alma.Status

let onStatusChange : OnStatusChange =
    fun change ->
        printfn "%A -> %A (%A)" change.Instance change.Status change.ResourceKind

let startMonitoring (loggerFactory: ILoggerFactory) (systems: SoftwareSystem list) =
    let workflows =
        systems
        |> List.collect (StatusStorage.registerSystem onStatusChange loggerFactory)

    // Run under the host's supervision (services loop every 15s, data objects every 1h).
    workflows |> List.iter (Async.Start)

let readState () =
    let allItems = StatusStorage.statuses ()
    let systemsOnly = StatusStorage.systemStatuses ()
    let delta = StatusStorage.changes () // drains updates since the previous call
    allItems, systemsOnly, delta
```

## Lag-Aware Severity

Reclassify a consumer check by Kafka lag. Production resolves lag from Kafka
(`CurrentLag = None`); tests inject a value.

```fsharp
open Alma.Kafka
open Microsoft.Extensions.Logging
open Alma.Status

let lagSeverity (loggerFactory: ILoggerFactory) (injectedLag: int64 option) : Severity =
    let streamLag =
        StreamLagSeverity.StreamLag.create
            (BrokerList "kafka:9092")
            (StreamName "demo-events")
            50L
            (GroupId "demo-worker")

    let dependencies : StreamLagSeverity.Dependencies = {
        LoggerFactory = loggerFactory
        // None -> query Kafka (production); Some -> inject (tests).
        CurrentLag = injectedLag |> Option.map (fun lag -> fun _ -> lag)
    }

    StreamLagSeverity.idleOnUnhealthyUpToLag dependencies streamLag

// Attach to a consumer check:
let consumerCheck (loggerFactory: ILoggerFactory) check =
    check |> lagSeverity loggerFactory None
```

## Produce Incidents

Wrap a check so it publishes an incident event for `Warning`/`Critical` results.
The adapter returns `None` for results that should not produce an event.

```fsharp
open Alma.Kafka
open Alma.Status

let enrich
    (createIncidentMessage: IncidentManagement.CreateIncidentMessage<'App>)
    (currentApplication: 'App)
    (produce: IncidentManagement.ProduceEvent)
    environment
    system
    service
    check
    =
    IncidentManagement.produceIncidentEvent
        createIncidentMessage
        currentApplication
        produce
        environment
        system
        service
        check
```

## Replay And Consume

Rebuild incident history on startup from a file, and from a Kafka stream.

```fsharp
open Microsoft.Extensions.Logging
open Alma.Kafka
open Alma.Status

let replayFromFile
    (loggerFactory: ILoggerFactory)
    (parse: IncidentManagement.ParseIncidentEvent)
    (filePath: string)
    : Async<unit> =
    IncidentManagement.loadIncidentsFromFile loggerFactory parse filePath

let consumeFromStream
    (loggerFactory: ILoggerFactory)
    (consume: History.ConsumeIncidentEvents)
    : Async<unit> =
    History.consumeIncidents loggerFactory consume {
        BrokerList = BrokerList "kafka:9092"
        Topic = StreamName "demo-events"
    }

let readIncidents () = History.incidents ()
```

## Full Workflow Assembly

A host startup module gathers every workflow it needs to supervise.

```fsharp
open Microsoft.Extensions.Logging
open Alma.Kafka
open Alma.Status

type MonitoringWorkflows = {
    StatusChecks: Async<unit> list
    IncidentConsumer: Async<unit> option
    IncidentReplay: Async<unit> option
}

let buildWorkflows
    (loggerFactory: ILoggerFactory)
    (onStatusChange: OnStatusChange)
    (systems: SoftwareSystem list)
    (consume: History.ConsumeIncidentEvents option)
    (parse: IncidentManagement.ParseIncidentEvent option)
    : MonitoringWorkflows =
    {
        StatusChecks =
            systems
            |> List.collect (StatusStorage.registerSystem onStatusChange loggerFactory)

        IncidentConsumer =
            consume
            |> Option.map (fun c ->
                History.consumeIncidents loggerFactory c {
                    BrokerList = BrokerList "kafka:9092"
                    Topic = StreamName "demo-events"
                })

        IncidentReplay =
            parse
            |> Option.map (fun p ->
                IncidentManagement.loadIncidentsFromFile loggerFactory p "./incidents.ndjson")
    }
```
