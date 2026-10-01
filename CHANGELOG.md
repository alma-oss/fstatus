# Changelog

<!-- There is always Unreleased section on the top. Subsections (Add, Changed, Fix, Removed) should be Add as needed. -->
## Unreleased
- Fix: `healthCheckForStream` no longer caches a timed-out or empty topic list; failures read `Streams unavailable: <reason>` instead of `Stream does not exist`, and the topic list is cached for 1 min (was 30).

## 2.0.0 - 2026-08-19
- [**BC**] ConsumeIncidentEvents now takes a ConsumerConfiguration instead of a general ConnectionConfiguration.
- Add `OnStatusChange.resourceAvailability` helper that enables/disables `Alma.Metrics` resource availability based on status changes
- Update dependencies

## 1.0.0 - 2026-05-14
- Initial implementation
