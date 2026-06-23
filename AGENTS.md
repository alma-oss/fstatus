# AGENTS.md — fstatus (Alma.Status)

## Agent Skills

This repo ships Agent Skill for the `Alma.Status` library. Compatible agents discover it automatically; see `.agents/skills/fstatus/SKILL.md`.

## Project Purpose

F# library (`Alma.Status`) for running health checks over services and data objects, aggregating system status, tracking status changes, and collecting incident history. It is typically embedded into service-monitoring or status-page applications rather than run as a standalone service.

## Install (for consumers)

Add to `paket.references`:

```text
Alma.Status
```

Current published version: **2.3.0** (`Status.fsproj` `<Version>`).

## Tech Stack

| Component            | Detail                                       |
| -------------------- | -------------------------------------------- |
| Language             | F# on .NET 10.0                              |
| SDK                  | .NET SDK 10.x                                |
| Build system         | FAKE via `build/build.fsproj`                |
| Package manager      | Paket                                        |
| Logging              | `Microsoft.Extensions.Logging`               |
| Error handling       | `Feather.ErrorHandling` (`AsyncResult`)      |
| Shared contracts     | `Alma.Status.Common`                         |
| Service identity     | `Alma.ServiceIdentification`                 |
| Environment modeling | `Alma.EnvironmentModel`                      |
| State                | `Alma.State`, `Alma.State.ConcurrentStorage` |
| Kafka integration    | `Alma.Kafka`                                 |
| HTTP/OAuth checks    | `Alma.WebApplication`                        |
| Lint                 | `fsharplint`                                 |

## Commands

```bash
# Restore dependencies and tools
dotnet tool restore
dotnet tool run paket restore

# Build
./build.sh build

# Lint
./build.sh lint

# Run tests
./build.sh tests
```

Build options:
- `no-clean` — skip cleaning output directories
- `no-lint` — run lint target but ignore lint failures

## Project Structure

```text
fstatus/
├── Status.fsproj                  # Main library project (PackageId: Alma.Status)
├── README.md                      # Package overview and basic usage notes
├── CHANGELOG.md                   # Release notes
├── build.sh                       # Tool restore + Paket restore + FAKE entrypoint
├── src/
│   ├── Types.fs                   # Core domain types, check results, severities
│   ├── HealthCheck.fs             # HTTP, OAuth, Kafka stream, and metric-based checks
│   ├── ServiceCheck.fs            # Executes checks and folds statuses per component
│   ├── StreamLagSeverity.fs       # Reclassifies consumer health using Kafka lag
│   ├── StatusStorage.fs           # In-memory current state + periodic polling loops
│   ├── IncidentManagement.fs      # Incident event adapters and file replay helpers
│   └── History/
│       └── History.fs             # Aggregated incident history storage and ingestion
├── build/
│   ├── Build.fs                   # FAKE build entrypoint
│   ├── Targets.fs                 # Shared FAKE targets
│   └── ...
└── .github/workflows/
    ├── tests.yaml                 # PR + nightly test workflow
    ├── pr-check.yaml              # Fixup-commit block + ShellCheck
    └── publish.yaml               # Tag-driven NuGet publish
```

## Architecture & Key Concepts

### Core Types (`Alma.Status.Types`)

- `SoftwareSystem` groups `ServiceToCheck` and `DataObjectToCheck` under a logical system name.
- `Check` is the execution primitive: `{ Name; Execute: Async<CheckResult> }`.
- `CheckResult` maps to common DTO status levels: `Success`, `Info`, `Warning`, `Critical`.
- `Severity` is a function `Check -> Check` used to transform the meaning of check outcomes after execution is defined.
- `Severity.criticalAfterTime` suppresses immediate critical results until the same check has been failing long enough.

### Health Checks (`Alma.Status.HealthCheck`)

- Provides reusable constructors for HTTP-based checks against local k8s service URLs and public URLs.
- Supports secured endpoint verification where `401/403` is considered healthy for protected resources.
- Supports OAuth-backed health checks by obtaining Cognito tokens before calling the target endpoint.
- Includes Kafka topic existence checks via `Alma.Kafka.Admin`.
- Includes Prometheus-style metric parsing and helpers for checking metrics across multiple endpoints, including StatefulSet pod DNS patterns.

### Check Execution (`Alma.Status.ServiceCheck`)

- Executes all checks for services or data objects in parallel.
- Folds multiple check results into a single component status using the `Alma.Status.Common.Status.fold` rules.
- Returns `AsyncResult<(Instance * Status) list, string list>` so callers can log or aggregate failures without losing batch context.

### Stream Lag Severity (`Alma.Status.StreamLagSeverity`)

- Allows a consumer service to be treated as idle or unhealthy depending on Kafka lag.
- `idleOnUnhealthyUpToLag` rewrites service check results using a lag threshold for a `(broker, stream, groupId)` tuple.
- Lag can be injected for tests or resolved from Kafka in production through `Admin.lags`.

### Status Storage (`Alma.Status.StatusStorage`)

- Maintains in-memory current status for all registered systems in process-wide concurrent state.
- Registers systems with initial `"Not checked yet"` warning states.
- `registerSystem onStatusChange loggerFactory system` returns `PeriodicCheck list` (= `Async<unit> list`) — two long-running polling loops per system that callers must supervise.
- `onStatusChange` has type `OnStatusChange = StatusChange -> unit`, where `StatusChange` carries `ResourceKind` (`ServiceResource | DataObjectResource`), `Instance`, and `Status`.
  - service checks every 15 seconds
  - data-object checks every 1 hour
- Exposes three read functions:
  - `statuses()` — all current `StatusItem` values
  - `systemStatuses()` — rolled-up system-level states
  - `changes()` — delta since the previous `changes()` call

### Incident History (`Alma.Status.History`)

- Stores aggregated incident history keyed by `(System, Instance)`.
- Only `Warning` and `Critical` check results become incident records.
- Repeated incident events are coalesced by incrementing occurrence counts and extending the time window.
- Supports two ingestion modes:
  - replay from an external event source via `consumeIncidents`
  - replay from newline-based files via `IncidentManagement.loadIncidentsFromFile`

### Incident Adapters (`Alma.Status.IncidentManagement`)

- Does not define a fixed incident schema for applications.
- Instead, callers provide a function that turns a check result into an optional Kafka `MessageToProduce`.
- This keeps the library generic while still supporting incident-stream publication.

### Status Aggregation

- `Alma.Status.Common` defines the public DTO contracts for system, service, data-object, and incident payloads.
- `Status.add` and `Status.fold` implement worst-status-wins aggregation semantics used throughout the library.

## Key Dependencies

| Package / Project              | Role                                                            |
| ------------------------------ | --------------------------------------------------------------- |
| `Alma.Status.Common`           | Shared status and incident DTOs                                 |
| `Microsoft.Extensions.Logging` | Logging abstraction for checks and polling loops                |
| `Feather.ErrorHandling`        | `AsyncResult` combinators and helpers                           |
| `Alma.ServiceIdentification`   | `Instance`, `Service`, tags, and naming helpers                 |
| `Alma.EnvironmentModel`        | Environment and tier modeling                                   |
| `Alma.Kafka`                   | Stream existence checks, lag inspection, incident-message types |
| `Alma.WebApplication`          | HTTP calls and OAuth token retrieval                            |
| `Alma.State`                   | Temporary cache and concurrent in-memory state                  |

## Conventions

- Namespace is `Alma.Status` for the library.
- Small single-case DU wrappers are used for names such as `CheckName` and `SystemName`.
- Companion modules expose helpers like `value`, `create`, and `map`.
- Status aggregation is monotonic toward the worst status: `Normal < Info < Warning < Critical`.
- Check execution is asynchronous; higher-level batch APIs usually return `AsyncResult`.
- State is process-local and mutable through `Alma.State.ConcurrentStorage`, not persisted.
- The library is adapter-oriented: callers supply logging, incident production, resource-availability hooks, and environment-specific URL/credential inputs.

## CI/CD

| Workflow        | Trigger                      | What it does                                                 |
| --------------- | ---------------------------- | ------------------------------------------------------------ |
| `tests.yaml`    | Pull requests + nightly cron | Runs `./build.sh -t tests` on `ubuntu-latest` with .NET 10.x |
| `pr-check.yaml` | Pull requests                | Blocks fixup commits and runs ShellCheck                     |
| `publish.yaml`  | Git tags matching `x.y.z`    | Runs `./build.sh -t publish` with `NUGET_API_KEY`            |

## Release Process

1. Increment `<Version>` in `Status.fsproj`
2. Update `CHANGELOG.md`
3. Commit and create a git tag matching the version
4. Push the tag so CI publishes the package

## Pitfalls

- There is currently no `tests/` project in this repo. The FAKE `Tests` target prints `There are no tests yet.` unless a test project is added.
- `Alma.Status.Common` is a required external dependency. If building in isolation without access to this dependency, the build will fail.
- `StatusStorage` and `History` use global in-memory state. They are process-local and can retain data across repeated registrations inside the same process.
- Service polling and data-object polling run forever once started. Consumers of `registerSystem` are responsible for scheduling and supervising those async loops.
- The `build/` directory is shared FAKE boilerplate used across Alma repositories. Keep changes there narrow and deliberate.
- Incident aggregation only records `Warning` and `Critical` results. `Success` and `Info` events do not become incident history entries.
