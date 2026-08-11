# PostgreSQL blocking chains — design note

Status: **not started.** Decisions below are the ones worth making before writing code; none needs Erik
unless flagged.

The SQL Server side has three blocking surfaces (`dmv_blocking_snapshots`, `blocked_process_reports`,
`get_blocking`). PostgreSQL has none of it yet, and blocking is the condition people actually call about.

## What the source looks like

`pg_stat_activity` plus `pg_blocking_pids(pid)`. Unlike SQL Server there is no ring-buffer equivalent and no
server-side threshold that materialises a report — nothing is recorded unless someone looks. So this is a
**sampling** collector: it captures who is blocked, by whom, on what, at the moment it runs. That has a
consequence worth stating in the collector's own docs, because it will otherwise be mistaken for a
blocked-process-report equivalent: **blocking shorter than the cadence is invisible.** The SQL Server
collector catches a 6-second block via the ring buffer; a 1-minute PostgreSQL sample will not.

## Decisions

**1. `pg_blocking_pids()` is not free — call it selectively.** It takes ShareLock on the lock manager
partitions per call. Calling it for every row in `pg_stat_activity` on a 5,000-connection instance is
exactly the kind of monitoring query that becomes the incident. Call it only for backends that are already
waiting on a lock: `WHERE wait_event_type = 'Lock'`. That is the population that can have blockers, so the
filter loses nothing and bounds the cost to the actually-blocked set.

**2. Store the edge list, not a rendered tree.** `pg_blocking_pids()` returns an array; unnest it to one row
per (blocked_pid, blocking_pid) pair. Rendering a chain at collection time bakes in one view of it, and the
interesting questions (root blocker, chain depth, fan-out) are all cheap over an edge list and expensive to
recover from a string. The reader assembles the tree — same division as the existing readers computing
ratios the collector deliberately does not.

**3. Capture the blocker's own state, not just its pid.** A chain whose root is `idle in transaction` is a
different problem from one whose root is a long-running query, and the pid alone does not say which. Both
sides of each edge need `state`, `wait_event_type`/`wait_event`, `xact_start`, `query_start` and the query
text. This is the single most common gap in homegrown PostgreSQL blocking monitoring — you get a pid and
have to go find out what it was doing, by which time it is gone.

**4. Query text is a truncation decision, not an afterthought.** `pg_stat_activity.query` is capped by
`track_activity_query_size` (1 KB default). Store what the server gives and do NOT try to join out to
`pg_stat_statements` for the full text — the queryid is not exposed on `pg_stat_activity` before PG14, and
even after, correlating a live backend to a normalised entry is a different claim than "this is what it
ran". Record the truncation instead so a reader knows the text may be clipped.

**5. Applies to: any PostgreSQL target, INCLUDING standbys.** Recovery conflicts are real blocking and a
standby is where they happen. Do not inherit the autovacuum collector's `IsInRecovery` gate — that gate
exists because `pg_stat_user_tables` reports zeros on a replica, which does not apply here.
`pg_stat_activity` on a standby reports that standby's own backends, which is what you want.

**6. Cadence: 1 minute, retention 30 days, and do NOT set a lock timeout.** The SQL Server snapshot
collector sets a 1-second `LOCK_TIMEOUT` and yields rather than joining a blocking chain
(`YieldsOnLockTimeout`). That guard exists because it reads DMVs that can themselves block. Reading
`pg_stat_activity` does not take table locks, so there is nothing to yield on and declaring the flag would
add a branch that can never fire. (If a future version reads something heavier, revisit — the classifier
already maps 55P03 to a yield, and that branch is currently unreachable for PostgreSQL.)

**7. Permissions.** `pg_monitor` is enough to see other backends' `query` text. Without it
`pg_stat_activity.query` shows `<insufficient privilege>` for backends the login does not own — worth a
note in the collector, since it degrades to a useless capture rather than an error.

## Shape

Payload roughly: `blocked_pid`, `blocking_pid`, then for each side `state`, `wait_event_type`,
`wait_event`, `query`, `xact_duration_ms`, `query_duration_ms`, `application_name`, `client_addr`,
`database_name`, `username`. Plus `blocked_pid_count` per blocker so the reader can rank fan-out without a
self-join. Timestamps come from `pg_stat_activity` as timestamptz — **use `AT TIME ZONE 'UTC'`**, per the
trap that already bit twice on this branch, or prefer storing durations computed server-side.

## Read surface

`get_pg_blocking` — root blockers first, ranked by how many backends they are blocking and for how long,
with each root's own state spelled out (the `idle in transaction` case named explicitly, since the remedy is
"fix the application", not "tune the query"). Chain depth per edge. Follow the existing pattern: severity
classified in the tool, a distinct explanation per blocker state, and no claim the collector cannot support.

## Verification, per the established habit

Ladder-generator diff for the rung; `probe_collector_sql_live.py` against stage 16.11 and 17.7 (this one
genuinely needs the live run — `pg_blocking_pids()` behaviour and the `<insufficient privilege>` degradation
are both things to see rather than assume); `probe_validate_reader_sql.py` with a synthetic edge list that
includes a two-level chain, a fan-out root, and an `idle in transaction` root.
