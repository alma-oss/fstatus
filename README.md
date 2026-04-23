F-Status
========

[![NuGet](https://img.shields.io/nuget/v/Alma.Status.svg)](https://www.nuget.org/packages/Alma.Status)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Alma.Status.svg)](https://www.nuget.org/packages/Alma.Status)
[![Tests](https://github.com/alma-oss/fstatus/actions/workflows/tests.yaml/badge.svg)](https://github.com/alma-oss/fstatus/actions/workflows/tests.yaml)

`Alma.Status` is an F# library for:

- running health checks for services and data objects,
- aggregating component status into system-level status,
- tracking status changes over time,
- collecting and replaying incident history.

It is designed to be embedded into your app (status dashboard backend, service monitor, operations API), not run as a standalone service.

## Install

Add to `paket.references`:

```text
Alma.Status
```

## Concepts

- `Check` is an async function returning `Success`, `Info`, `Warning`, or `Critical`.
- `ServiceToCheck` and `DataObjectToCheck` group checks per component.
- `SoftwareSystem` groups related services and data objects.
- `OnStatusChange` is a callback invoked after each component check. It receives a `StatusChange` record:
  - `ResourceKind` — `ServiceResource` or `DataObjectResource`
  - `Instance` — the checked component instance
  - `Status` — the resulting status
- `StatusStorage` keeps current in-memory state and exposes:
  - `statuses()` for all current items,
  - `systemStatuses()` for rolled-up system states,
  - `changes()` for delta snapshots since last call.
- `History` aggregates warning/critical incidents.

## Quick Start

### 1) Define checks and systems

```fsharp
open System
open Microsoft.Extensions.Logging
open Alma.Status
open Alma.Status.HealthCheck
open Alma.ServiceIdentification

let systemName = SystemName "Payments"
let systemService = {
	Domain = Domain "example"
	Context = Context "payments"
}

// Replace this helper with however your application constructs
// Alma.ServiceIdentification.Instance values.
let getInstance (name: string) : Instance =
	Instance.parse "-" name
	|> Option.defaultWith (fun () -> failwith "Invalid instance")

let webApiInstance = getInstance "payments-api"
let redisInstance = getInstance "payments-cache"

let mkSystem (loggerFactory: ILoggerFactory) : SoftwareSystem =
	let webChecks =
		[ healthCheck loggerFactory [] "HEAD" webApiInstance
		  healthCheckSecuredResource loggerFactory "GET" webApiInstance
			|> Severity.criticalAfterTime (TimeSpan.FromMinutes 5.0) ]

	let redisMetricsUrls = k8sSTSPodsAtPort 9121 3 redisInstance "/metrics"
	let redisChecks =
		[ healthCheckMetricAboveZero loggerFactory "connected_replicas" redisMetricsUrls redisInstance
			|> Severity.notCritical ]

	{
		Name = systemName
		Service = systemService
		Tags = [ Tag "platform" ]
		Services =
			[ {
				Name = "Payments API"
				Instance = webApiInstance
				Tags = [ Tag "eks"; Tag "public" ]
				StatusPage = K8sInternalService "/status"
				Checks = webChecks
			  } ]
		DataObjects =
			[ {
				Name = "Cache"
				Instance = redisInstance
				Tags = [ Tag "redis"; Tag "internal" ]
				Checks = redisChecks
				Details = []
			  } ]
	}
```

### 2) Build periodic check workflows

```fsharp
open Microsoft.Extensions.Logging
open Alma.Status

let onStatusChange: OnStatusChange =
	fun statusChange ->
		printfn "%A changed to %A (%A)" statusChange.Instance statusChange.Status statusChange.ResourceKind

let statusCheckWorkflows
	(loggerFactory: ILoggerFactory)
	(onStatusChange: OnStatusChange)
	(systems: SoftwareSystem list)
	=
	systems
	|> List.collect (StatusStorage.registerSystem onStatusChange loggerFactory)
```



`StatusStorage.registerSystem` returns long-running async workflows. In a real app, you would run them under your host's supervision model.

Each registered system produces two polling loops:

- service checks every 15 seconds,
- data-object checks every 1 hour.

### 3) Read current status and changes

```fsharp
open Alma.Status

let allItems = StatusStorage.statuses ()
let systemsOnly = StatusStorage.systemStatuses ()
let delta = StatusStorage.changes ()
```

`changes()` returns only updates since the previous `changes()` call.

## Incident History

Only `Warning` and `Critical` check results become incidents.

The library is decoupled from any specific Kafka event schema. You supply two adapters:

- `CreateIncidentMessage<'Application>` — maps a check result to a `MessageToProduce option` for your event topic.
- `ParseIncidentEvent` — deserialises a raw string into a `History.IncidentEvent`.

### Produce incident events

Wrap a check with `IncidentManagement.produceIncidentEvent` to optionally publish a Kafka message whenever the check yields a `Warning` or `Critical` result:

```fsharp
open Alma.Kafka
open Alma.Status

let enrichedCheck
    (createIncidentMessage: IncidentManagement.CreateIncidentMessage<MyApplication>)
    (produce: IncidentManagement.ProduceEvent)
    environment
    system
    service
    check
    =
    IncidentManagement.produceIncidentEvent
        createIncidentMessage
        MyApplication.current
        produce
        environment
        system
        service
        check
```

`createIncidentMessage` returns `None` for check results that should not produce events (e.g. `Success`/`Info`).

### Build incident consumer workflow

```fsharp
open Microsoft.Extensions.Logging
open Alma.Kafka
open Alma.Status

let incidentConsumerWorkflow
	(loggerFactory: ILoggerFactory)
	(consume: History.ConsumeIncidentEvents)
	(brokerList: BrokerList)
	(stream: StreamName)
	=
	History.consumeIncidents loggerFactory consume {
		BrokerList = brokerList
		Topic = stream
	}
```

### Build incident replay workflow

```fsharp
open Microsoft.Extensions.Logging
open Alma.Status

let incidentReplayWorkflow
	(loggerFactory: ILoggerFactory)
	(parseIncidentEvent: IncidentManagement.ParseIncidentEvent)
	(filePath: string)
	=
	IncidentManagement.loadIncidentsFromFile loggerFactory parseIncidentEvent filePath
```

For example, an app startup module can assemble everything it needs to run:

```fsharp
type MonitoringWorkflows = {
	StatusChecks: Async<unit> list
	IncidentConsumer: Async<unit> option
	IncidentReplay: Async<unit> option
}

let buildMonitoringWorkflows
	(loggerFactory: ILoggerFactory)
	(onStatusChange: OnStatusChange)
	(systems: SoftwareSystem list)
	(consume: History.ConsumeIncidentEvents option)
	(parseIncidentEvent: IncidentManagement.ParseIncidentEvent option)
	=
	{
		StatusChecks =
			statusCheckWorkflows loggerFactory onStatusChange systems

		IncidentConsumer =
			consume
			|> Option.map (fun consumeIncidentEvents ->
				incidentConsumerWorkflow
					loggerFactory
					consumeIncidentEvents
					(BrokerList "kafka:9092")
					(StreamName "orders-events"))

		IncidentReplay =
			parseIncidentEvent
			|> Option.map (fun parse ->
				incidentReplayWorkflow loggerFactory parse "./incidents.ndjson")
	}
```

### Read aggregated incidents

```fsharp
open Alma.Status

let incidents = History.incidents ()
```

## Stream Lag-Aware Severity

Use `StreamLagSeverity.idleOnUnhealthyUpToLag` when a consumer can be considered idle if lag is below a threshold:

```fsharp
open Alma.Kafka
open Alma.Status

let lagSeverity loggerFactory =
	let streamLag =
		StreamLagSeverity.StreamLag.create
			(BrokerList "kafka:9092")
			(StreamName "orders-events")
			50L
			(GroupId "payments-worker")

	let dependencies: StreamLagSeverity.Dependencies =
		{
			LoggerFactory = loggerFactory
			CurrentLag = None
		}

	StreamLagSeverity.idleOnUnhealthyUpToLag dependencies streamLag
```

Attach the returned severity transformer to a `Check` with `|> severity`.

## Development

### Requirements

- [.NET SDK](https://dotnet.microsoft.com/)

### Build

```bash
./build.sh build
```

### Lint

```bash
./build.sh lint
```

### Tests

```bash
./build.sh tests
```

## Release

1. Increment version in `Status.fsproj`
2. Update `CHANGELOG.md`
3. Commit and tag the release
