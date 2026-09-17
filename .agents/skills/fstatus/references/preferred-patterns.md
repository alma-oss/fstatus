# Preferred Patterns — Alma.Status

## Core Principles

- A `Check` is just a name plus an `Async<CheckResult>`. Build checks from the
  `HealthCheck` constructors when possible; only hand-roll a `Check` record for
  custom logic (see `examples.md` → Basic Custom Check).
- Status is monotonic toward the worst result: `Normal < Info < Warning < Critical`.
  Folding many check results yields the worst one; you do not fold manually — the
  library does it per component.
- Severity transforms meaning *after* execution. A `Severity` is `Check -> Check`,
  so it composes left-to-right with `|>` on any check.
- All higher-level state (current status, incident history) is process-local and
  in-memory. Treat the host process as the source of truth, not a database.

## Recommended API Usage

- **HTTP checks**: prefer `healthCheck` (internal k8s service) and
  `healthCheckSecuredResource` (treats `401`/`403` as healthy for protected
  endpoints). Use `healthCheckWithOAuth` when a Cognito token is required, and
  `healthCheckOnPath` / `healthCheckSecuredResourcePath` to target a non-default path.
- **Kafka**: `healthCheckForStream` verifies a topic exists.
- **Metrics**: `healthCheckMetricAboveZero` parses a Prometheus-style metric and
  succeeds when its summed value across endpoints is `> 0`. Build the endpoint
  list with `k8sServiceAtPort` (single service) or `k8sSTSPodsAtPort` (every pod
  of a StatefulSet). See `examples.md` → Realistic System.
- **Composition**: assemble `ServiceToCheck` / `DataObjectToCheck` lists into a
  `SoftwareSystem`, then hand it to `StatusStorage.registerSystem`.
- **Reading state**: `statuses()` returns every current item, `systemStatuses()`
  returns rolled-up system states, and `changes()` returns only the delta since
  the previous `changes()` call.

## Error Handling

- Component check execution returns `AsyncResult<(Instance * Status) list, string list>`,
  so a failing batch surfaces errors as a string list without losing the other
  results. Log the error list; do not assume a single failure aborts the system.
- Individual HTTP checks never throw for an unhealthy endpoint — they map the
  failure into a `Warning`/`Critical` `CheckResult`. A failed HTTP check is
  rechecked once after a short delay before being reported as critical, so a
  single transient blip is reported as a warning rather than critical.
- Use the `Feather.ErrorHandling` `AsyncResult`/`asyncResult` combinators when
  wrapping your own async work into a `Check`.

## Composition

- Chain severity transformers onto a check with `|>`, e.g. a metric check piped
  into `Severity.notCritical`, or a flaky check piped into
  `Severity.criticalAfterTime`. See `examples.md` → Severity Composition.
- `Severity.criticalAfterTime delay` suppresses an immediate `Critical`, keeping
  the check at `Warning` until it has been failing continuously for `delay`.
- `Severity.notCritical` downgrades `Critical` to `Warning` for non-critical
  components.
- `StreamLagSeverity.idleOnUnhealthyUpToLag` is a severity that reinterprets a
  consumer check using Kafka lag (idle when lag is within budget, critical when
  lag exceeds it). See `examples.md` → Lag-Aware Severity.

## Integration With Other Libraries

- Pass an `ILoggerFactory` into every constructor that needs one; checks and
  polling loops create named loggers from it.
- Build `Instance` values with `Alma.ServiceIdentification` helpers — never
  construct cluster URLs by hand; use the `k8s*` helpers which derive them from an
  `Instance`.
- Incident publication is adapter-driven: supply a `CreateIncidentMessage<'App>`
  that maps a check result to an optional Kafka message, and a `ParseIncidentEvent`
  that deserialises raw strings. The library never fixes an incident schema.
- `StreamLagSeverity.Dependencies.CurrentLag = None` resolves lag from Kafka
  (recommended in production); supply `Some` to inject a value.

## Naming Conventions

- Names are single-case DU wrappers: `CheckName`, `SystemName`, plus identifiers
  from related libraries (`Instance`, `Service`, `Tag`, `GroupId`, `StreamName`,
  `BrokerList`). Wrap and unwrap with the companion module's `value` / `create`.
- Derive a per-instance check name with `CheckName.ofInstance baseName instance`
  so names stay unique across components.
- Modules are `[<RequireQualifiedAccess>]`; call helpers qualified
  (`CheckResult.warning`, `Check.success`, `Severity.notCritical`).

## Testing Recommendations

- Inject `StreamLagSeverity.Dependencies.CurrentLag = Some (fun _ -> lag)` in tests
  so severity logic is exercised without a live Kafka broker. See
  `examples.md` → Lag-Aware Severity.
- Use `Check.success name` for a trivial always-passing check, and the
  `CheckResult.success` / `info` / `warning` / `critical` helpers to build expected
  results.
- Because storage and history are process-global, isolate stateful assertions per
  test run (fresh process or unique instances) so prior registrations do not leak.
