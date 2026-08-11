# PostgreSQL alerting — scoping note

Status: **design decision needed before implementation.** Not started.

The three Tier 0 outage predictors (`pg_wraparound_stats`, `pg_xmin_horizon`, `pg_replication_slots`) are
collected, stored and readable through MCP. Nothing pages anyone about them. That is the largest remaining
gap for a monitoring replacement — each names a condition that stops the server outright, and each is
silent until it is nearly too late, which is exactly the profile that needs an alert rather than a
dashboard.

## Why this is not another vertical slice

The previous seven collectors were additive: new file, three registrations, a migration rung, a reader, a
tool. Nothing existing changed behaviour. Alerting is not like that. The alert engine is **shared with
Lite** and is what SQL Server monitoring alerts through today, so this is the highest-blast-radius area on
the branch.

A single new alert touches:

| Surface | File | Note |
|---|---|---|
| Read contract | `PerformanceMonitor.Alerting/IAlertReadAdapter.cs` | **the decision point — see below** |
| Lite implementation | `Lite/Services/LiteAlertReadAdapter.cs` | Lite has no PostgreSQL target |
| Darling implementation | `Darling/.../DarlingAlertReadAdapter.cs` | the real one |
| Info record | `PerformanceMonitor.Alerting/<X>Info.cs` | per signal |
| Evaluation | `PerformanceMonitor.Alerting/AlertEngine.cs` (1533 lines) | threshold + edge-trigger + dedup |
| Settings contract | `PerformanceMonitor.Alerting/IAlertEngineSettings.cs` | `<X>Enabled` + threshold, per the `Pvs*` pattern |
| File config | `Darling/.../DarlingConfig.cs` `alerts` section | plus README |
| Store config | `config_alert_settings` columns | **needs a migration** |
| Viewer | Settings window | Erik drives GUI work |
| Tests | `AlertEngineTests`, `DarlingAlertReadAdapterTests`, `LiteAlertForwardingTests`, `BlockingDeadlockContextBuilderTests` | all four reference the adapter |

Follow the `Pvs*` naming (`PvsEnabled` / `PvsThresholdPercent` / `PvsFloorGb`) — it is the most recently
added signal and the cleanest template.

## The decision

**Where does a PostgreSQL-only signal live on a contract Lite also implements?**

*Option A — extend `IAlertReadAdapter`.* One contract, one engine path, consistent with every existing
signal. Cost: Lite must implement three methods for an engine it cannot monitor, returning empty. That is
dead code in the Lite SKU forever, and it invites the next person to wonder whether Lite is supposed to
grow PostgreSQL support.

*Option B — a separate `IPostgresAlertReadAdapter`, consulted only when the target engine is PostgreSQL.*
Keeps Lite untouched and makes the engine gate explicit, mirroring how `CollectorCatalog.AppliesTo` already
gates collection by engine. Cost: two read contracts, and `AlertEngine` grows an engine branch.

**Recommendation: B**, for the same reason the collector seam gates by engine rather than having every
definition claim every target — the alternative puts empty stubs in a shipping SKU to satisfy a contract
it has no stake in. But this is an architecture call on the shared brain, and per house rules that is
Erik's, not mine.

## What the thresholds should be, once the seam exists

Not arbitrary — these come from what the collectors already measure:

- **Wraparound.** Alert on `xid_age` against `autovacuum_freeze_max_age`, not on a raw XID count. The
  useful thresholds are the two the engine itself acts on: approaching `autovacuum_freeze_max_age`
  (autovacuum will force a wraparound-prevention vacuum) and approaching the hard 2-billion stop. Warn at
  the first, critical well before the second, since the remedy is hours of vacuuming.
- **xmin horizon.** Alert on the WINNING holder's age plus its persistence across the window — the
  collector already attributes the cause, and the four causes need different fixes, so the alert must name
  the holder rather than the number. A chronic holder is the alert; a query that ran long is not.
- **Replication slots.** Alert on `wal_status` plus whether retained WAL is GROWING, which is the
  distinction `get_pg_replication_slots` already computes. `lost`/`unreserved` are critical on their face;
  `extended` + inactive + growing is the disk-fill emergency; `extended` alone is a warning.

Each of those maps to a severity the read surface already computes, so the engine's job is threshold +
edge-trigger + dedup, not re-deriving the finding.

## Also worth knowing

`pg_autovacuum_stats` gates OFF standbys because `pg_stat_user_tables` reports all zeros on an Aurora
reader (measured: writer 13,654,458 dead tuples, reader 0). Any alert reading autovacuum state inherits
that gate, or it will report perfect health for a badly-behind cluster. The same per-instance caveat
applies to slots — they live on the writer, so an empty result from a replica is not a cluster all-clear.
