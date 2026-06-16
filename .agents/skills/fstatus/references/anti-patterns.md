# Anti-Patterns — Alma.Status

Each entry is **mistake → why → fix**.

## Ignoring the returned polling workflows

- **Mistake**: calling `StatusStorage.registerSystem` and discarding its result,
  expecting checks to run on their own.
- **Why**: it returns a list of long-running `Async<unit>` workflows (one services
  loop, one data-objects loop per system). Nothing polls until the host runs them.
- **Fix**: collect the returned workflows and start them under the host's
  supervision model. See `examples.md` → Register And Run.

## Treating `changes()` as an idempotent read

- **Mistake**: calling `changes()` repeatedly expecting the same delta, or calling
  it in multiple places.
- **Why**: `changes()` drains the change set — it returns updates since the
  previous call and then clears them.
- **Fix**: have exactly one consumer of `changes()`; use `statuses()` /
  `systemStatuses()` for repeatable full reads.

## Expecting every check to create an incident

- **Mistake**: assuming `Success` or `Info` results show up in `History.incidents()`.
- **Why**: only `Warning` and `Critical` results become incident records; healthy
  results are intentionally dropped.
- **Fix**: rely on incident history only for warnings/criticals; use status reads
  for the full picture.

## Returning a message for healthy results in the incident adapter

- **Mistake**: writing a `CreateIncidentMessage` that always returns `Some message`.
- **Why**: the adapter is the filter — returning `Some` for `Success`/`Info`
  publishes noise events that will never become history anyway.
- **Fix**: return `None` for results that should not produce an event; return
  `Some` only for the levels you publish. See `examples.md` → Produce Incidents.

## Forgetting state is process-global

- **Mistake**: registering the same system multiple times in one process and
  expecting a clean slate, or sharing one process across isolated test cases.
- **Why**: `StatusStorage` and `History` use process-wide concurrent state that
  persists across repeated registrations.
- **Fix**: register each system once; isolate stateful tests with unique instances
  or a fresh process.

## Hitting Kafka from unit tests via lag severity

- **Mistake**: leaving `Dependencies.CurrentLag = None` in tests of
  `idleOnUnhealthyUpToLag`.
- **Why**: `None` makes it query Kafka synchronously for the real lag, which is
  slow and unavailable in tests.
- **Fix**: inject `Some (fun _ -> lag)` in tests; keep `None` only in production.
  See `examples.md` → Lag-Aware Severity.

## Hand-building cluster URLs

- **Mistake**: string-concatenating pod or service URLs for metric/HTTP checks.
- **Why**: the in-cluster DNS pattern (StatefulSet pod ordinals, service slug) is
  non-obvious and easy to get wrong across environments.
- **Fix**: derive URLs from an `Instance` with `localUrlWithPath`,
  `k8sServiceAtPort`, or `k8sSTSPodsAtPort`.

## Misordering curried health-check arguments

- **Mistake**: passing `method` and `instance` in the wrong order, or omitting the
  `headers` argument to `healthCheck`.
- **Why**: constructors are curried with a fixed parameter order (e.g.
  `healthCheck loggerFactory headers method instance`); a swap compiles but checks
  the wrong thing or fails at runtime.
- **Fix**: follow the exact signatures shown in `examples.md` → Realistic System.

## Reaching for a non-existent persistence layer

- **Mistake**: querying `statuses()` / `incidents()` expecting durable history
  after a restart.
- **Why**: there is no persistence; all state is in-memory and lost on process
  exit.
- **Fix**: if durability is needed, replay incidents on startup via
  `loadIncidentsFromFile` or `consumeIncidents`, and treat status as ephemeral.
  See `examples.md` → Replay And Consume.
