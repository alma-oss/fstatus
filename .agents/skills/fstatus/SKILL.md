---
name: fstatus
description: >-
  Use whenever generating or reviewing F# code that builds health checks,
  aggregates component/system status, tracks status changes, or records incident
  history with the Alma.Status library. Trigger on mentions of healthCheck,
  healthCheckSecuredResource, healthCheckWithOAuth, healthCheckForStream,
  healthCheckMetricAboveZero, k8sSTSPodsAtPort, Severity.criticalAfterTime,
  Severity.notCritical, StatusStorage.registerSystem, statuses/systemStatuses/changes,
  StreamLagSeverity.idleOnUnhealthyUpToLag, produceIncidentEvent,
  loadIncidentsFromFile, consumeIncidents, OnStatusChange, SoftwareSystem,
  ServiceToCheck, DataObjectToCheck, Check, CheckResult, or composing severity
  transformers over checks.
---

# F-Status

Library: [https://github.com/alma-oss/fstatus](https://github.com/alma-oss/fstatus)
NuGet: `Alma.Status`

## Purpose

`Alma.Status` is an F# library for running async health checks over services and
data objects, folding their results into component and system-level status,
tracking status changes over time, and collecting/replaying incident history. It
is embedded into a host application (status dashboard backend, monitor, ops API),
not run as a standalone service.

## When to Use

- Defining `Check` values or composing reusable health checks (HTTP, secured,
  OAuth, Kafka stream existence, Prometheus-style metrics).
- Grouping checks into `ServiceToCheck` / `DataObjectToCheck` / `SoftwareSystem`.
- Registering periodic polling and reading current status or deltas.
- Reclassifying check severity (e.g. delayed-critical, lag-aware idle).
- Producing, consuming, or replaying incident events.

## When NOT to Use

- Defining business/domain models or status-page UI rendering.
- Choosing a logging, Kafka, or HTTP client implementation (those are injected via
  related libraries).
- Persisting status: state is process-local and in-memory only.

## Main Concepts

- **Check** — `{ Name: CheckName; Execute: Async<CheckResult> }`, the execution primitive.
- **CheckResult** — `Success | Info | Warning | Critical` of a `CheckMessage`.
- **Severity** — a `Check -> Check` transformer that reinterprets a check's result.
- **ServiceToCheck / DataObjectToCheck** — a named component with an `Instance`, `Tags`, and `Checks`.
- **SoftwareSystem** — groups related services and data objects under a `SystemName`.
- **HealthCheck module** — constructors for HTTP, secured, OAuth, stream, and metric checks plus k8s URL helpers.
- **OnStatusChange** — `StatusChange -> unit` callback invoked after each component check.
- **StatusStorage** — in-memory current state, periodic polling loops, and read APIs (`statuses`, `systemStatuses`, `changes`).
- **StreamLagSeverity** — reclassifies a consumer check based on Kafka consumer-group lag.
- **IncidentManagement** — adapters to publish check results as incident events.
- **History** — aggregates `Warning`/`Critical` incidents keyed by component, coalescing repeats.

## Related Libraries

- `Alma.Status.Common` — shared `Status`/`StatusItem`/incident DTOs and folding rules.
- `Alma.ServiceIdentification` — `Instance`, `Service`, `Tag`, naming helpers.
- `Alma.EnvironmentModel` — `Environment`, `Tier` modeling.
- `Alma.Kafka` — broker/stream config, admin lag/topic queries, message types.
- `Alma.WebApplication` — HTTP calls and OAuth/Cognito token retrieval.
- `Alma.State` — concurrent in-memory state and temporary cache.
- `Feather.ErrorHandling` — `AsyncResult` combinators.
- `Microsoft.Extensions.Logging` — `ILoggerFactory` injected into checks and loops.

## Keywords for Search

health check, status aggregation, F# monitoring, Check, CheckResult, Severity,
criticalAfterTime, notCritical, SoftwareSystem, ServiceToCheck, DataObjectToCheck,
OnStatusChange, StatusStorage, registerSystem, statuses, changes, StreamLagSeverity,
consumer lag, incident history, produceIncidentEvent, consumeIncidents,
loadIncidentsFromFile, k8sSTSPodsAtPort, healthCheckMetricAboveZero, Kafka, AsyncResult.

## Reference Files

- For composition principles, recommended API usage, error handling, integration,
  naming, and testing guidance, read `references/preferred-patterns.md`.
- For known pitfalls, incorrect assumptions, and legacy usage to avoid, read
  `references/anti-patterns.md`.
- For worked, self-contained code examples (the only place code lives), read
  `references/examples.md`.
