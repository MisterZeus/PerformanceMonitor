# Performance Monitor Darling — Headless Edition

Darling is the headless, centralized edition of Performance Monitor: a 24/7 Windows service that collects from your SQL Servers into a central PostgreSQL (optionally TimescaleDB) store, plus a detached desktop viewer that reads that store. No desktop app has to stay open for collection to happen, and every viewer seat reads the same central data.

It runs the **same monitoring brain as the Lite edition** — one shared codebase, two storage engines:

- `PerformanceMonitor.Collectors` owns all 38 collector definitions: the exact T-SQL sent to monitored servers, the result-row mappings, the delta rules, the default cadences and retention horizons, and the ignored-wait-types list. Lite writes those rows to DuckDB; Darling writes the same rows to PostgreSQL via binary COPY.
- `PerformanceMonitor.Alerting` owns the shared alert engine — the same thresholds, edge-trigger gates, cooldowns, and dedup fingerprints Lite uses.
- The analysis/recommendations pipeline (the same inference engine behind both apps' Recommendations tabs and the `analyze_server` MCP tool) runs on a schedule inside the service.

A collector, alert, or analysis change lands once in the shared libraries and both editions get it. A Darling install monitoring a server even derives the **same `server_id`** Lite would for that server, because the identity rule (`host[:database][:RO]`, hashed) is shared too.

> **Status: in development.** Darling builds and runs from source (it is wired into the solution and CI), but is not yet packaged into the signed release artifacts. Expect the surface documented here to grow.

---

## When to Choose Darling vs. Lite

| | **Lite** | **Darling** |
|---|---|---|
| Collection runs | While the desktop app is open (or in the tray) | 24/7 as a Windows service |
| Data lives | Locally per seat (DuckDB + Parquet) | Centrally (PostgreSQL / TimescaleDB) |
| Execution plans | Not stored (fetched live when you view a query) | Captured and stored, TOAST-compressed (`capturePlans`, default on) |
| Viewers | The app is the viewer | Any number of viewer seats read the central store |
| Setup | Download and run | Provision PostgreSQL, edit `darling.json`, install the service |
| Best for | Quick triage, consultants, a handful of servers | Always-on team monitoring, larger estates, one shared store |
| Configuration | Settings UI | One JSON file (no UI) |

Nothing is installed on the monitored SQL Servers by either edition beyond two lightweight Extended Events ring-buffer sessions and, when it is unset, a one-time `blocked process threshold` bootstrap (see [What the Service Does on Monitored Servers](#what-the-service-does-on-monitored-servers)).

---

## Quick Start

### Prerequisites

- **Windows** for the service host (Windows-service lifetime, DPAPI password protection) and for the viewer (WPF). Monitored servers can be SQL Server 2016–2025, Azure SQL Managed Instance, AWS RDS for SQL Server, or Azure SQL Database.
- **A PostgreSQL store — bundled or your own.** In managed mode (the shipped default, see [Managed Bundled PostgreSQL](#managed-bundled-postgresql)) the service runs its own bundled PostgreSQL 18 + TimescaleDB and no database provisioning is needed. To bring your own instead, PostgreSQL 16 or newer is recommended (developed and validated against PostgreSQL 18) with a database and a login the service can create tables in.
- **TimescaleDB is optional and auto-adopted.** If the extension is installed (or pre-created by an administrator) in the store database, the service detects it at startup and automatically converts the collector tables to hypertables with compression; without it, the service runs in plain-PostgreSQL mode, which is fully supported. No configuration flag either way.
- **.NET 10** to build and run.

Build from the repository root:

```
dotnet build Darling/PerformanceMonitor.Darling.Service/PerformanceMonitor.Darling.Service.csproj -c Release
```

```
dotnet build Darling/PerformanceMonitor.Darling.Viewer/PerformanceMonitor.Darling.Viewer.csproj -c Release
```

### Configure darling.json

The service reads one JSON file. It resolves the path in this order:

1. An explicit path (when a component is handed one)
2. The `DARLING_CONFIG` environment variable
3. `darling.json` next to the service binary

Copy the shipped `darling.sample.json` (it lands next to the built binary) to `darling.json` and edit. Comments and trailing commas are allowed; property names are case-insensitive.

Minimal working example — one server, integrated auth, bring-your-own PostgreSQL. (With the bundled store instead, replace the `postgres` block with `"postgres": { "managed": true }` and skip provisioning entirely — see [Managed Bundled PostgreSQL](#managed-bundled-postgresql).)

```json
{
  "postgres": {
    "connectionString": "Host=localhost;Port=5432;Username=darling;Database=darling"
  },
  "servers": [
    {
      "name": "SQL2022",
      "host": "SQL2022",
      "auth": "integrated",
      "excludedDatabases": []
    }
  ]
}
```

**Integrated auth (recommended).** The service connects to monitored servers as the Windows account the service runs under. Grant that account the [permissions below](#permissions-on-monitored-servers).

**SQL auth.** Set `"auth": "sql"`, a `username`, and an `encryptedPassword` produced by the `--encrypt-password` verb:

```
PerformanceMonitor.Darling.Service.exe --encrypt-password
```

It prompts for the password on stdin (so the plaintext never lands in your shell history) and prints a base64 DPAPI blob. Paste that blob into the server's `"encryptedPassword"`. The blob is protected with **DPAPI LocalMachine scope**, so an administrator can encrypt it interactively and the service account can decrypt it later on the same machine — but it is machine-bound: run `--encrypt-password` **on the machine that will run the service**, and re-encrypt if you move `darling.json` to another machine. A plaintext `"password"` also works as a dev convenience, but the service logs a warning every time it is used.

**excludedDatabases** (per server) removes databases from collection: per-database collectors skip them and the exclusion is spliced into the collector queries — the same filter Lite applies. There is a second, separate `alerts.excludedDatabases` list that excludes databases from blocking/deadlock/long-running-query **alert evaluation** without affecting collection.

### Validate the Config (Pre-flight)

Before installing the service, check that `darling.json` is well-formed and that every monitored server is reachable with the configured credentials:

```
PerformanceMonitor.Darling.Service.exe --test-connection
```

(`--validate-config` is an alias.) It validates the file, then connects to and probes each server, printing a `[PASS]`/`[FAIL]` line per server (SQL major version, engine edition, and whether the account has msdb access for failed-job alerts). It exits `0` only when the file is valid **and** every server is reachable, so it doubles as a deployment gate. Add an explicit config path as a second argument if `darling.json` is not next to the exe and `DARLING_CONFIG` is not set. This is the same probe the Viewer's **Test Connection** button runs through the service.

### Run It — Console Mode

The same executable serves interactive debugging and service installation; the Windows-service lifetime is a no-op when run from a console.

```
Darling\PerformanceMonitor.Darling.Service\bin\Release\net10.0\PerformanceMonitor.Darling.Service.exe
```

Watch the log output: you should see the config load (`Loaded configuration from ...`), the store migrate (`Postgres store ready (schema v34, ...)`), the TimescaleDB detection result, per-server connects, and then per-collector run lines with row counts.

### Install as a Windows Service

**Scripted (recommended):** the packaged zips ship `install-darling.ps1` beside the service exe. Extract the zip to its final location (e.g. `C:\PerformanceMonitorDarling`), then from an elevated PowerShell in that folder run `.\install-darling.ps1`. It checks for `darling.json` (copying the sample and stopping for you to edit it on first run), runs the `--test-connection` pre-flight, registers the Event Log source, creates the service under the virtual account (or upgrades an existing install's binPath in place, preserving config/store/credentials), starts it, and creates Desktop + Start Menu **Darling Viewer** shortcuts (pin to taskbar from the Start Menu entry — Windows does not allow programmatic pinning). `uninstall-darling.ps1` reverses it, deliberately leaving the store/config in place unless you pass `-PurgeData`.

**Manual:** publish (or copy the build output) to a stable path, put `darling.json` next to the exe (or set `DARLING_CONFIG` as a machine environment variable), then register it:

```
dotnet publish Darling/PerformanceMonitor.Darling.Service/PerformanceMonitor.Darling.Service.csproj -c Release -o C:\PerformanceMonitorDarling
```

```
sc create "PerformanceMonitor Darling" binPath= "C:\PerformanceMonitorDarling\PerformanceMonitor.Darling.Service.exe" start= auto obj= "NT SERVICE\PerformanceMonitor Darling"
```

```
sc start "PerformanceMonitor Darling"
```

Also register the service's Windows event source once, from the same elevated shell — event-source registration requires elevation, and the virtual service account cannot do it itself (without this, Event Log diagnostics are silently dropped; the file log under `%ProgramData%\PerformanceMonitorDarling\logs` works regardless):

```
powershell -NoProfile -Command "New-EventLog -LogName Application -Source 'PerformanceMonitor Darling' -ErrorAction SilentlyContinue"
```

The `obj=` clause runs the service under a **virtual service account** (`NT SERVICE\<service name>` — password-less, per-service SID, unprivileged; the same convention SQL Server itself uses). That is the right account for SQL-auth monitoring, and with `postgres.managed = true` it is more than a preference: PostgreSQL refuses to execute with administrative privileges, so don't run the service as LocalSystem — a least-privilege account keeps the bundled store's initdb/start path on ground PostgreSQL supports. For integrated auth to monitored servers, set a domain account (or gMSA) holding the SQL-side grants below instead, via **Services.msc → Log On** or `sc config ... obj=`. Note the space after `binPath=`, `start=`, and `obj=` — `sc` requires it.

One managed-mode handoff gotcha: if you test-drove the service from a console first, the bundled store's data directory belongs to *your* account, and the service account may not be able to write it. Point the service at a fresh `postgres.dataDirectory` (or delete the test directory) rather than fighting ACLs.

### What the Service Does on Monitored Servers

On each successful connect, the service:

1. **Probes the server** — one query against `sys.dm_os_sys_info` / `SERVERPROPERTY()` for version, engine edition (box / Managed Instance / Azure SQL DB), AWS RDS detection, and msdb access. It is the same detection query Lite runs, so both editions classify a server identically.
2. **Ensures two Extended Events ring-buffer sessions** (created if missing, started if stopped; ~4 MB ring buffer each, no files written on the server):
   - `PerformanceMonitor_Deadlock` — `xml_deadlock_report`, server-scoped on on-prem/Managed Instance/RDS; `database_xml_deadlock_report`, database-scoped on Azure SQL Database.
   - `PerformanceMonitor_BlockedProcess` — `blocked_process_report`, server-scoped (database-scoped on Azure SQL Database).
3. **Bootstraps the blocked-process threshold** — if `blocked process threshold (s)` is `0`, the service sets it to `5` via `sp_configure`. On AWS RDS `sp_configure` is unavailable; the attempt is tolerated and logged, and you set the threshold through an RDS Parameter Group instead (Azure SQL Database has a fixed 20-second threshold).
4. **Runs the on-connect config snapshots once** (`server_config`, `database_config`, `database_scoped_config`, `trace_flags`, `server_properties`), then runs all scheduled collectors on the shared default cadences.

Every failure in steps 2–3 is tolerated and logged: the deadlock/blocked-process collectors simply read zero rows until the sessions exist (and blocked-process reports only start arriving once the threshold is set). Monitoring queries connect with a 15-second connect budget and an application name of `PerformanceMonitorDarling`; connection encryption fails closed to `Mandatory` when the configured mode is unrecognized.

### Permissions on Monitored Servers

```sql
USE [master];
CREATE LOGIN [DarlingMonitor] WITH PASSWORD = N'YourStrongPassword';
GRANT VIEW SERVER STATE TO [DarlingMonitor];
GRANT ALTER ANY EVENT SESSION TO [DarlingMonitor];

-- Only if the instance hosts Availability Groups (see the table below)
GRANT VIEW ANY DEFINITION TO [DarlingMonitor];

-- Optional: SQL Agent job monitoring + failed-job alerts
USE [msdb];
CREATE USER [DarlingMonitor] FOR LOGIN [DarlingMonitor];
ALTER ROLE [SQLAgentReaderRole] ADD MEMBER [DarlingMonitor];
```

| Grant | Why | If missing |
|---|---|---|
| `VIEW SERVER STATE` | All DMV collectors (wait stats, query stats, memory, CPU, file I/O, sessions, etc.) and the connect probe | Collection fails — this one is required |
| `ALTER ANY EVENT SESSION` | Create/start the two XE sessions | Logged; deadlock and blocked-process collectors read zero rows (an admin can pre-create the sessions instead) |
| `ALTER SETTINGS` | The `sp_configure` blocked-process-threshold bootstrap | Logged; set the threshold yourself (or via RDS Parameter Group) |
| `SQLAgentReaderRole` on msdb | `running_jobs` collector and the failed/long-running-job alerts | Skipped gracefully — logged as a permissions skip, alerts return no jobs |
| `DBCC TRACESTATUS` permission | `trace_flags` snapshot | Degrades to zero rows with a warning |
| `VIEW ANY DEFINITION` | The AG catalog views (`sys.availability_groups`, `sys.availability_replicas`) the `ag_replica_states` / `ag_database_replica_states` collectors join to the `sys.dm_hadr_*` DMVs | **Silently zero rows** — catalog views hide rows rather than erroring, so a real AG cluster looks identical to a server with no AGs. Not needed on an instance without Availability Groups |

**Azure SQL Database:** connect to the one database you monitor (set the server entry's `"database"`), using a contained user with `VIEW DATABASE STATE`, matching the product's existing Azure guidance. The XE sessions are created database-scoped there (`ALTER ANY DATABASE EVENT SESSION`); SQL Agent collectors are skipped automatically.

Collectors that hit a permission error (SQL errors 229/297/300) log a `PERMISSIONS` row in `collection_log` and retry on their next scheduled run — one denied collector never stops the rest.

#### Which collectors run on which platform

Every collector declares its own applicability in code (`AppliesTo(CollectorTargetInfo)`), so this is not a hand-maintained list of 36 rows — the collectors fall into five groups, and a collector outside its supported platform is **skipped before it runs**, not failed and logged every cycle.

| Runs on | Collectors | Gate |
|---|---|---|
| Everything | wait stats, CPU utilization, memory (stats/clerks/grants), file I/O, tempdb, latches, spinlocks, plan cache, session summary, plus blocking, deadlocks, blocked-process reports, DMV blocking snapshots, perfmon, query snapshots, procedure stats, index/object stats, long-query completions, database config/scoped-config/size, server properties, session stats, waiting tasks | no gate |
| On-prem, Managed Instance, RDS — **not** Azure SQL DB | CPU scheduler stats, default trace events, memory pressure events, server config, system health events, trace flags | `!IsAzureSqlDb` |
| On-prem and Managed Instance, needs msdb | job history | `!IsAzureSqlDb && HasMsdbAccess` |
| On-prem and Managed Instance, needs msdb — **not** RDS | agent status, running jobs | `!IsAzureSqlDb && !IsAwsRds && HasMsdbAccess` |
| SQL Server 2016+ (or any Azure flavour) | query stats, Query Store stats | `SqlMajorVersion >= 13 \|\| IsAzureSqlDb \|\| IsAzureManagedInstance` |

Notes:

- **Azure SQL DB** is the most restricted target: the six `!IsAzureSqlDb` collectors read server-scoped DMVs or on-disk artifacts that do not exist there, and the SQL Agent collectors have no Agent to read. Nothing about that is a permission problem, so it is not reported as one.
- **AWS RDS** blocks direct `msdb` job reads specifically; the rest of the SQL Agent surface is unaffected.
- **`HasMsdbAccess`** is probed per server at connect (`HAS_DBACCESS('msdb')`), so losing the `SQLAgentReaderRole` grant later moves those collectors from running to skipped without an error storm.
- An unknown version (`SqlMajorVersion == 0`, i.e. detection has not completed yet) is treated as capable rather than skipped, so a collector is never silently dropped because a probe was slow.

If a tab or column is empty and you expect data, check **Collection Health**: a collector skipped for platform reasons shows no runs at all, whereas one denied by permissions logs `PERMISSIONS` and is classified `NO_PERMISSIONS`. Those are different problems with different fixes — the first is expected on that platform, the second is a grant to add from the table above.

---

## Configuration Reference

All sections except `postgres` and `servers` are optional — omit a section (or any key) to get the defaults listed here. Defaults deliberately mirror a fresh Lite install.

### postgres

Two mutually exclusive modes — setting both `managed: true` and `connectionString` is a validation error:

| Key | Default | Notes |
|---|---|---|
| `managed` | `false` | `true` runs the bundled PostgreSQL + TimescaleDB (Windows only; see [Managed Bundled PostgreSQL](#managed-bundled-postgresql)). The connection string is derived, never configured. |
| `port` | `5641` | Managed mode only: the loopback port the bundled server listens on. Deliberately uncommon so it coexists with any PostgreSQL (5432) already on the machine. |
| `dataDirectory` | *(null)* | Managed mode only: the cluster's data directory. `null` means `%ProgramData%\PerformanceMonitorDarling\pg`. |
| `connectAs` | `"admin"` | Managed mode only: which least-privilege role the Viewer connects as — `"admin"` (reads everything + manages mute rules and dismisses alerts) or `"viewer"` (read-only; those write actions are hidden/disabled). See [Security & Least-Privilege Roles](#security--least-privilege-roles). Ignored in bring-your-own mode (the connection string picks the role). |
| `connectionString` | *(required unless managed)* | Npgsql connection string for a store you provision yourself, e.g. `Host=localhost;Port=5432;Username=darling;Password=...;Database=darling` |

### servers (array, at least one entry)

| Key | Default | Notes |
|---|---|---|
| `name` | `""` | Display name; falls back to `host` |
| `host` | *(required)* | Server/instance to monitor |
| `database` | *(none)* | Azure SQL Database only: the one database this entry monitors (also part of the server's storage identity) |
| `auth` | `"integrated"` | `"integrated"` or `"sql"` |
| `username` | *(none)* | Required for `"sql"` |
| `encryptedPassword` | *(none)* | DPAPI blob from `--encrypt-password` (preferred) |
| `password` | *(none)* | Plaintext fallback — dev only, warned on every use |
| `readOnlyIntent` | `false` | Route to a readable AG secondary (`ApplicationIntent=ReadOnly`) |
| `trustServerCertificate` | `false` | |
| `encryptMode` | `"Mandatory"` | `Mandatory` / `Strict` / `Optional`; unknown values fail closed to `Mandatory` |
| `multiSubnetFailover` | `false` | |
| `excludedDatabases` | `[]` | Databases excluded from collection |

### capturePlans (boolean, optional)

| Key | Default | Notes |
|---|---|---|
| `capturePlans` | `true` | Capture execution plans into `query_stats.query_plan_xml` and `query_store_stats.query_plan_text`. PostgreSQL TOAST compresses the plan text transparently (LZ4 on the managed store) and TimescaleDB chunk compression squeezes it further, so plans are cheap to keep — unlike Lite, which stores to DuckDB/Parquet and deliberately never captures them. Set `false` to skip plan capture (e.g. to shave storage across a very large fleet). |

### collectSchemaChangeEvents (boolean, optional)

| Key | Default | Notes |
|---|---|---|
| `collectSchemaChangeEvents` | `true` | Record `Object:Created` / `Object:Altered` / `Object:Deleted` schema-change (DDL) events in the built-in default-trace collector. Set `false` on a noisy or benchmark box where a create/drop-happy workload floods the viewer's **System Events > Default Trace** tab — e.g. HammerDB's TPC-H Query 15 creates and drops a `revenue` view thousands of times, and the collector faithfully records every create/delete. Only the Object DDL slice is suppressed; file auto-grow/shrink, ErrorLog, and security-audit events are still collected. The shared collector's equivalent of the full Dashboard's `@include_object_events`. A file-only knob (not stored in the control plane): edit and restart. |

### alerts

The shared alert engine's switches and thresholds. Every default mirrors Lite's alert defaults exactly, so an empty section alerts like a fresh Lite install. `enabled: false` turns off all alert evaluation **and** scheduled-analysis finding notifications (the analysis itself still runs and persists findings).

| Key | Default | Meaning |
|---|---|---|
| `enabled` | `true` | Master switch for alert evaluation + finding notifications |
| `cpuEnabled` | `true` | |
| `cpuThresholdPercent` | `80` | |
| `cpuMode` | `"total"` | `"total"` = SQL + other processes; `"sql"` = SQL process only |
| `blockingEnabled` | `true` | |
| `blockingCountThreshold` | `1` | Blocked-process count (rolling window) that trips the alert |
| `deadlockEnabled` | `true` | |
| `deadlockCountThreshold` | `1` | Deadlock count (rolling window) that trips the alert |
| `poisonWaitEnabled` | `true` | THREADPOOL / RESOURCE_SEMAPHORE / RESOURCE_SEMAPHORE_QUERY_COMPILE |
| `poisonWaitThresholdMs` | `500` | Average ms per wait |
| `longRunningQueryEnabled` | `true` | |
| `longRunningQueryThresholdMinutes` | `30` | |
| `tempDbSpaceEnabled` | `true` | |
| `tempDbSpaceThresholdPercent` | `80` | |
| `lowDiskEnabled` | `true` | Volume free space; graded CRITICAL when critically low |
| `lowDiskThresholdPercent` | `10` | Fire below X% free; `0` disables this dimension (clamped 0–100) |
| `lowDiskThresholdGb` | `5` | Fire below X GB free; `0` disables this dimension |
| `longRunningJobEnabled` | `true` | SQL Agent job running long vs. its history |
| `longRunningJobMultiplier` | `3` | Fires at 3x the job's historical average |
| `failedJobEnabled` | `true` | Live msdb check for recently failed jobs |
| `failedJobLookbackMinutes` | `60` | Clamped 1–1440 |
| `cooldownMinutes` | `5` | Minimum minutes between repeats of the same alert condition (clamped 1–120) |
| `excludedDatabases` | `[]` | Excluded from blocking/deadlock/long-running-query **alert evaluation** (collection unaffected) |

Not configurable (hardcoded to Lite's defaults until someone needs a knob): the long-running-query read shape (top 5 results; the five noise filters — sp_server_diagnostics, WAITFOR, backups, misc waits, CDC — all on) and the analysis-finding notification policy (notify at severity >= 1.5, 6-hour per-finding cooldown).

### smtp

Email delivery is enabled when `host`, `from`, and `to` are all set — there is no separate enable flag.

| Key | Default | Notes |
|---|---|---|
| `host` | `""` | |
| `port` | `587` | |
| `useSsl` | `true` | |
| `username` | *(none)* | For authenticated relays |
| `encryptedPassword` | *(none)* | Same `--encrypt-password` DPAPI pattern as SQL auth |
| `from` | `""` | |
| `to` | `""` | Comma-separated recipients |
| `emailCooldownMinutes` | `15` | Email/webhook channel cooldown (clamped 1–120) |

### webhooks

A channel is enabled by a non-empty URL.

| Key | Default | Notes |
|---|---|---|
| `teamsUrl` | `""` | Teams incoming webhook |
| `teamsProxy` | `""` | Optional proxy address |
| `slackUrl` | `""` | Slack incoming webhook |
| `slackProxy` | `""` | Optional proxy address |

### mcp

The embedded MCP server, over Streamable HTTP bound to `localhost` by default (see [Opt-in Network Endpoints (LAN)](#opt-in-network-endpoints-lan) to reach it — and the store — from the LAN). It exposes the same tool names Lite and the Dashboard expose, plus small Darling-only WRITE surfaces — Custom Views management, alert tuning, and server onboarding (see the last three bullets):

- **Six diagnostic-analysis tools** — `analyze_server`, `get_analysis_facts`, `compare_analysis`, `audit_config`, `get_analysis_findings`, `mute_analysis_finding`.
- **Five plan-analysis tools** — `analyze_query_plan` (by `query_hash`), `analyze_procedure_plan` (by `sql_handle`), `analyze_query_store_plan` (by `database_name` + `query_id`), `analyze_plan_xml` (raw showplan XML, no fetch), and `get_plan_xml` (raw stored plan XML by `query_hash`). These run the shared execution-plan analyzer over the plan XML the collectors already captured into the store — a stored-plan read, never a live query against the monitored server. `analyze_query_plan`/`get_plan_xml` accept an optional `database_name`, and `analyze_query_store_plan` an optional `plan_id`, to pin the exact stored plan when the caller knows it.
- **Fifteen core data-read tools** — the diagnostic reads an assistant needs to investigate a server, each a stored read of the collected data (never a live query against the monitored server):
  - *Resource metrics* — `get_cpu_utilization`, `get_wait_stats`, `get_wait_trend`, `get_wait_types` (the distinct observed wait types, to pick one for `get_wait_trend`), `get_memory_stats`, `get_memory_clerks`, `get_file_io_stats`, `get_tempdb_trend`, `get_perfmon_stats`.
  - *Query performance* — `get_top_queries_by_cpu`, `get_top_procedures_by_cpu`, `get_query_store_top` (these hand back the `query_hash` / `sql_handle` / `query_id` + `plan_id` keys the plan-analysis tools consume).
  - *Discovery / health* — `list_servers` (with collection-freshness status), `get_collection_health`, `get_server_properties`.

  These are the tools the analysis findings' `next_tools` recommendations point at, so a client following a finding's advice resolves them on this same server. Result shapes match Lite's (the store is Lite's collector schema); where Lite and the Dashboard's shapes diverge, Darling follows Lite — the shape its collector-mirror store can serve faithfully.
- **Twenty diagnostic-depth data-read tools** — deeper reads for a blocking / deadlock / session / configuration / storage investigation, each a stored read:
  - *Blocking / deadlocks* — `get_blocking` (blocked/blocking pairs from the blocked-process-report XE + the always-on DMV fallback), `get_deadlocks`, `get_deadlock_detail` (raw graph XML), `get_blocked_process_xml` (raw report XML), and the per-minute count series `get_blocking_trend` / `get_deadlock_trend`.
  - *Sessions* — `get_session_stats` (latest per-application connection counts), `get_active_queries` (captured running-query snapshots), `get_waiting_tasks`.
  - *Config* — the change history `get_server_config_changes`, `get_database_config_changes`, `get_trace_flag_changes`, plus `get_database_scoped_config` (latest scoped snapshot) and the current-config snapshots `get_server_config` / `get_database_config` / `get_trace_flags` (what sp_configure / sys.databases / the active trace flags are set to **right now** — the companion to the `*_changes` diffs, which are empty on a stable server).
  - *Index / object* — `get_table_index_sizes` (size + growth), `get_index_usage` (Unused / Write-only / Active), `get_object_locking` (lock/latch contention), `get_database_sizes`.

  The three config-change tools diff the store's config snapshots. This edition captures configuration **when the service connects** to a server (not on a fixed schedule), so a change is detected between two connect snapshots and at least two are needed — a stable, always-connected deployment may show no changes until the next connect. They emit only the values the collectors capture; the Dashboard's `requires_restart` / setting `description` / `setting_type` / generated change-narrative enrichment is not collected here and is omitted. The Dashboard's `get_blocking_deadlock_stats` aggregate is **not** hosted (Darling has no blocking/deadlock rollup table — use `get_blocking` / `get_deadlocks` for the raw events).

- **Eight resource-contention + jobs data-read tools** — deeper reads for an internal-contention / worker-thread / plan-cache / SQL Agent investigation, each a stored read of the latest collected snapshot:
  - *Latch / spinlock* — `get_latch_stats` (top latch classes by wait time, per-second rates), `get_spinlock_stats` (top spinlocks by collisions).
  - *Memory grants* — `get_resource_semaphore` (workspace-memory target / max-target ceiling vs granted / used), `get_memory_grants` (per-pool grant detail), `get_memory_pressure_events` (RING_BUFFER_RESOURCE_MONITOR notifications — the process/system pressure indicators, not on Azure SQL DB).
  - *Plan cache / scheduler* — `get_plan_cache_bloat` (single-use vs multi-use + bloat level), `get_cpu_scheduler_pressure` (runnable queue, worker utilization, pressure level).
  - *Jobs* — `get_running_jobs` (running SQL Agent jobs vs historical average / p95).

  The Dashboard's per-class latch `severity` / `description` / `recommendation`, spinlock `description`, plan-cache `bloat_level`, and CPU-scheduler `pressure_level` / `recommendation` are the Dashboard / reporting-view CASE derivations (not collected columns), reproduced service-side so the full result shape is served. Darling's delta collectors store no `sample_interval_seconds`, so per-second latch/spinlock rates are derived from the collection interval, and the Dashboard's `get_resource_semaphore` `sample_interval_seconds` is not emitted for the same reason (`max_target_memory_mb`, the workspace-memory ceiling, is added since the store carries it).

- **Five trend data-read tools** — windowed time-series siblings of the core reads, each a stored read of the collected series over the window (BOTH-sides, naive-UTC):
  - `get_memory_trend` (total / target server memory, buffer pool, plan cache over time), `get_perfmon_trend` (a single counter's value + delta, `counter_name` required), `get_file_io_trend` (per-database read/write latency, top-10 busiest files), `get_query_trend` (one query's per-collection history by `query_hash` + `database_name`), `get_query_duration_trend` (overall elapsed-ms/sec + executions/sec).

  Each mirrors the viewer's proven chart read (byte-identical Postgres SQL); the shape follows Lite where the SKUs diverge. `get_perfmon_trend` reproduces Lite's miss vocabulary (Page Life Expectancy is intentionally not collected; an unknown counter hands back the collected names). `get_memory_trend` carries a `total_granted_mb` field for field-for-field parity with Lite, where its memory_stats-only read leaves it 0 (the grant overlay is a separate chart series).

- **Eight system-health parse-on-read tools** — the Dashboard's `get_health_parser_*` family, over Darling's raw `system_health_events`:
  - `get_health_parser_system_health` (corruption + contention counters), `get_health_parser_severe_errors` (severity ≥ 19, with `database_id` resolved to a name), `get_health_parser_scheduler_issues`, `get_health_parser_memory_conditions`, `get_health_parser_memory_broker`, `get_health_parser_memory_node_oom`, `get_health_parser_cpu_tasks`, `get_health_parser_io_issues`.

  Where the Dashboard reads its server-side-parsed `collect.HealthParser_*` tables, these shred the raw extended-event XML **on read** with the shared `SystemHealthParser` (the same parser the viewer's System Events tab uses) and gate with the service-side twin of the viewer's `SystemEventSignificance` — returning the same SIGNIFICANT warning set the Dashboard surfaces (sp_HealthParser at `@warnings_only = 1`). `get_health_parser_system_health` is the one UNGATED category (its counter series plots every snapshot). Each row carries the full sp_HealthParser column set keyed on the event's `event_time`; the tools window on `event_time` (the event's real time), so "last 24 hours" means events that happened in the last 24 hours.

- **Five alert + health-overview tools** — the fleet-triage reads the fleet edition previously lacked, each a stored read over the monitoring store (no live hit):
  - *Alerts* — `get_alert_history` (what fired, value vs threshold, delivery success/failure, muted — fleet-wide by default, or scoped to a server), `get_alert_settings` (the current alert config the service is using — per-alert enable/thresholds, cooldown, excluded databases, delivery mode, analysis cadence), `get_mute_rules` (the alert mute rules in force, so a suppressed server is distinguishable from a healthy-quiet one).
  - *Health overview* — `get_server_summary` (one-shot per-server CPU / memory / recent blocking / recent deadlocks), `get_daily_summary` (a day's composite health band — Healthy / Warning / Critical — folded through the shared `DailyHealthBandCalculator`, plus the signals behind it).

- **Eight Custom Views tools (Darling-only)** — discover, create, and manage the saved dashboards/notebooks a user composes from the curated measure catalog (the same views the web viewer's editor builds), stored in `config.custom_views`. None touches a monitored SQL Server or the collected performance data — the write tools write only view definitions to the monitoring store.
  - *Discover* — `describe_custom_view_catalog` (the compose vocabulary — measures with their source/kind/valid-aggregates/allowed-dimensions/units/per-server-type availability, dimensions, unit families, aggregates, time buckets, filter ops, and viz types). An MCP client calls this FIRST so a composed panel uses only legal identifiers instead of guessing at names; it returns the SAME `/api/catalog` vocabulary the web composer's picker binds to. Read-only static reference — no store, no server.
  - *Read* — `list_custom_views` (summaries: id, name, description, kind, version), `get_custom_view` (one view's full definition + version).
  - *Author* — `validate_custom_view` (dry-run a definition against the catalog + composer rules, no save), `create_custom_view` (validate then save), `update_custom_view` (validate then replace in place, optimistic-concurrency on `version`), `delete_custom_view`.
  - *Self-test* — `run_custom_view_panel` (compile + run a single composed panel and return `{sql, rows, annotations}` — the composer's live preview, for checking a generated panel's data before saving).

  The create/update/delete tools are the one view-authoring **write** surface; create/update run the SAME `ValidateDefinition` authority as `validate_custom_view`, so an invalid definition is rejected before it stores; every tool routes through the SAME store + validator + compile-and-run + catalog the web viewer's editor uses (no divergent second implementation). This write surface widens what an MCP token can do — see [Blast radius](#opt-in-network-endpoints-lan) below.

- **Three alert-tuning write tools (Darling-only)** — `update_alert_settings`, `create_mute_rule`, and `delete_mute_rule` let an MCP client TUNE the alert engine the fleet shares — the SAME config `get_alert_settings` / `get_mute_rules` read and the Viewer's Settings window writes. `update_alert_settings` is a PARTIAL update of the single global settings row: read via `get_alert_settings`, change fields, and send only those back in the same nested shape; every field is validated against the SAME ranges/enums the Settings window enforces BEFORE any write, an out-of-range or unknown field returns `{status:"invalid"}` and writes nothing, and the write self-bumps `config_version` so the running service hot-reloads within one collection sweep. `create_mute_rule` / `delete_mute_rule` reuse the SAME `PgMuteRuleStore` `get_mute_rules` reads through (and the same GUID id-generation the Viewer's mute-create path uses). None touches a monitored SQL Server or the collected data — only the shared alert configuration; SMTP/webhook delivery credentials are out of scope (the `mcp` role cannot read or write the secret columns). This widens what an MCP token can do — see [Blast radius](#opt-in-network-endpoints-lan) below.

- **Two server-onboarding write tools (Darling-only)** — `add_servers` (BULK) and `remove_server` let an MCP client stand up or tear down FLEET monitoring conversationally ("monitor these twenty servers with this login"), the service-side twin of the Viewer's Add / Manage Servers dialogs. `add_servers` takes a JSON **array** of server objects (`host` required; optional `display_name` / `database` / `read_only_intent` / `multi_subnet_failover`; `auth` `Windows`/`SQL` with `username`+`password` for SQL; and the exposed TLS options `encrypt_mode` `Optional`/`Mandatory`/`Strict` + `trust_server_certificate`) and processes them **in order**: it validates each entry, PROBES the connection in-process (reusing the same `DarlingServerConnector.ProbeAsync` the `--test-connection` verb runs — the service holds the network path + credentials, so no `test_connect` command plane is needed), skips a case-folded duplicate (`duplicate`) of an already-monitored server or an earlier entry, DPAPI-encrypts the SQL password (the service identity, so it round-trips at collection time), and INSERTs the row mirroring the service's own seed shape. A server that fails to connect is `connection_failed` and the batch continues; Entra/MFA/Service-Principal/Managed-Identity auth is `invalid` (the service connects with Windows or SQL only). `remove_server` DELETEs a monitored server by name (resolved the same way every `server_name` is) — already-collected history is kept. Both write only the monitoring store's `config.config_monitored_servers` registry; neither runs anything on a monitored server beyond the one-time probe. **The SQL password travels to the endpoint inside `add_servers`' request** and is DPAPI-encrypted at rest (never returned) — this widens what an MCP token can do and puts a credential on the wire; see [Blast radius](#opt-in-network-endpoints-lan) below.

| Key | Default | Notes |
|---|---|---|
| `enabled` | `false` | **Off by default** — a headless service does not open a local port unless you ask |
| `port` | `5152` | Chosen so all three editions coexist on one machine (Dashboard 5150, Lite 5151) |

Register with Claude Code:

```
claude mcp add --transport http --scope user sql-monitor-darling http://localhost:5152/
```

If the port is already in use at startup, the MCP server logs an error and does not start; collection is unaffected.

### web

The embedded read-only **web dashboard** — a browser view of the monitoring store, served over HTTP on its OWN port (default **5153**), separate from the MCP server. It is a distinct surface from [`### mcp`](#mcp): its own enable flag, port, token, and exposure block, because the two gate different blast radii (the MCP token guards `analyze_server`'s **live outbound** connections to your monitored SQL Servers; the web dashboard is **read-only over the collected store**). It connects to the store as the least-privilege `viewer` role. Loopback-only by default; see [Opt-in Network Endpoints (LAN)](#opt-in-network-endpoints-lan) to reach it from the LAN.

| Key | Default | Notes |
|---|---|---|
| `enabled` | `false` | **Off by default** — a headless service does not open a local port unless you ask |
| `port` | `5153` | Chosen so all four local surfaces coexist on one machine (Dashboard 5150, Lite 5151, Darling MCP 5152) |

Once enabled, open `http://localhost:5153/` in a browser on the service host. Like the MCP server, `enabled`/`port` here are the file SEED; after first start they live in the control plane and the Viewer's Settings toggles them LIVE (the service starts/stops/rebinds the dashboard within seconds — no restart). If the port is already in use at startup, the web host logs an error and retries on a calm cadence; collection is unaffected.

**What you see.** The dashboard opens on a **Fleet Overview**: a card per enabled server with a status dot, six per-metric health bands (CPU, threads, memory, blocking, deadlocks, collectors), and its last collection time — all banded server-side, so the browser only renders (a server that has never reported shows an amber "Awaiting first collection", never a red offline). Above the cards a worst-first "Needs attention" list surfaces the servers to look at, or an all-healthy line when there is nothing to chase. Click a card to **drill into one server**: an overview, wait stats with a trend for the heaviest wait, active queries, a CPU chart, memory and file-I/O trends, and collection health — the same collected data the viewer shows, over inline charts. A fleet-wide **Alert History** page (with a server filter box) rounds out phase 1. It is a read-only view — no settings, no write paths, no live-server queries — and refreshes every 60 seconds (pausing while the tab is hidden). The frontend ships fully self-contained (no CDN, no fonts, no remote anything), so it works on an air-gapped host with no internet access.

### No Schedule Knobs, by Design

There are deliberately **no collection-schedule or retention settings** in `darling.json`. The service consumes the shared per-collector defaults (`CollectorScheduleDefaults`) — the same cadences and retention horizons a fresh Lite install uses, identity-pinned by tests so the two editions cannot drift. If a schedule knob is ever genuinely needed, it will be added then, not speculatively.

---

## Operations

### The Store

The service migrates the store itself at startup — plain versioned SQL scripts, each applied once inside its own transaction, tracked in `darling_schema_version`, safe under concurrent starters (advisory-locked). Current schema is **v29**:

| Version | Contents |
|---|---|
| **V1** — collector tables | One table per collector, all 32, generated from the shared collector definitions (column-for-column identical to Lite's DuckDB schema): `wait_stats`, `latch_stats`, `spinlock_stats`, `query_stats`, `procedure_stats`, `query_store_stats`, `query_snapshots`, `plan_cache_stats`, `cpu_utilization_stats`, `cpu_scheduler_stats`, `file_io_stats`, `memory_stats`, `memory_clerks`, `memory_pressure_events`, `tempdb_stats`, `perfmon_stats`, `deadlocks`, `blocked_process_reports`, `dmv_blocking_snapshots`, `memory_grant_stats`, `waiting_tasks`, `session_stats`, `session_summary_stats`, `running_jobs`, `database_size_stats`, `index_object_stats`, `server_properties`, `system_health_events`, and the four config snapshots (`server_config`, `database_config`, `database_scoped_config`, `trace_flags`) |
| **V2** — observability | `servers` (registry, upserted on every successful connect: identity, display name, engine edition, major version) and `collection_log` (one row per collector run: SUCCESS / PERMISSIONS / ERROR, row count, SQL-phase and storage-phase timings) |
| **V3** — alerting | `config_alert_log` (one history row per fired alert), `config_edge_trigger_watermarks` (restart-surviving edge-trigger and failed-job watermarks), `config_mute_rules` (alert mute rules; starts empty) |
| **V4** — analysis | `analysis_findings` (persisted findings incl. the stored remediation action), `analysis_muted` (muted finding patterns), and 17 `v_<table>` passthrough views so the shared analysis SQL runs verbatim against this store |
| **V5** — viewer passthrough views | The five remaining `v_*` passthrough views (`v_running_jobs`, `v_server_config`, `v_database_scoped_config`, `v_trace_flags`, `v_collection_log`) that complete the viewer's read layer |
| **V6** — memory passthrough views | `v_memory_clerks` and `v_memory_pressure_events`, the two views the Memory tab reads |
| **V7** — plan-capture columns | Nullable plan-XML columns for the viewer's View Plan surfaces: `procedure_stats.query_plan_xml`, `blocked_process_reports.blocked_query_plan_xml` / `blocking_query_plan_xml`, `deadlocks.victim_query_plan_xml` |
| **V8** — schema split (collect/config) | Moves the tables into the `collect` and `config` schemas (least-privilege security split); the shared SQL keeps using bare names, resolved via `search_path = collect, config, public` |
| **V9** — inventory + cost fields | `server_properties` inventory columns (`sqlserver_start_time`, `host_os_version`, `ag_replica_role`) and `servers.monthly_cost_usd` (the FinOps per-server budget) |
| **V10** — latch + spinlock collectors | `latch_stats` and `spinlock_stats` tables plus their `v_*` views |
| **V11** — CPU scheduler + plan cache collectors | `cpu_scheduler_stats` and `plan_cache_stats` tables plus their `v_*` views |
| **V12** — session summary collector | `session_summary_stats` (server-wide connection-leak / idle signal) table plus its `v_*` view |
| **V13** — system health events collector | `system_health_events` (raw `system_health` Extended Events capture) table plus its `v_*` view |
| **V14** — refresh passthrough views | `CREATE OR REPLACE` on every `v_*` view so a store upgraded across a column-adding migration picks up the new columns (Postgres freezes a view's `SELECT *` expansion at create time) |
| **V15** — index metadata columns | Per-index definition columns on `index_object_stats` (ordered key/included column lists, filter, uniqueness/constraint/FK flags, `is_disabled`, and the reconstruct-a-CREATE options — compression, fill factor, page/row locks, etc.) for monitor-side UNUSED/DUPLICATE index analysis, and refreshes `v_index_object_stats` |
| **V16** — server UTC offset | Nullable UTC-offset column on `server_properties` so the viewer can render timestamps in the monitored server's own local time (the Server-time display mode ported from Lite; Server-time = stored naive-UTC + this offset) |
| **V17** — config control plane | The viewer-writable DESIRED-state tables (`config_service`, `config_monitored_servers`, `config_alert_settings`, `config_collector_schedules`) plus a `config_version` reload beacon — statement-level bump triggers increment it on any write, and the service polls that one integer each sweep and reloads only when it changes. Server secrets are DPAPI blobs, never plaintext |
| **V18** — alert delivery mode | Global `delivery_mode` (Summary / PerEvent) + `per_event_max` on `config_alert_settings`, plus a nullable per-server `alert_delivery_mode_override` on `config_monitored_servers` (null = inherit the global), resolved through the shared `AlertDeliveryModeResolver` (#1236 / #1141) |
| **V19** — analysis state marker | `collect.analysis_state` — the service-produced per-server "insufficient data" marker (with message + time) the viewer reads, so a not-enough-history analysis pass surfaces a reason instead of a blank |
| **V20** — alert tuning knobs | The previously-hardcoded alert tuning the viewer now customizes on `config_alert_settings`: the long-running-query read shape (`long_running_query_max_results` + five noise-filter opt-outs the shared `AlertEngine` forwards) and `notify_connection_changes` (the Server-Unreachable / Restored connect-edge gate) |
| **V21** — default trace events collector | `default_trace_events` table + its `v_*` view — the significant Default Trace events (file growth, ErrorLog, security audit, optional Object DDL) the viewer's System Events tab reads |
| **V22** — index-object latest index | The engine-agnostic `idx_index_object_stats_latest` partial index backing the latest-capture-per-index reads |
| **V23** — collection-log hypertable | Converts `collection_log` to a TimescaleDB hypertable (an object-invisible no-op on plain PostgreSQL) |
| **V24** — job history collector | `job_history` table + its `v_*` view — the SQL Agent Job History surface (#1433) |
| **V25** — agent status collector | `agent_status` table + its `v_*` view — SQL Agent up/down status (#1433) |
| **V26** — generic webhook channel | The generic-webhook columns on `config_notification` (`generic_url`, `generic_headers`, `generic_body_template`, `generic_proxy`) for POSTing alerts to any endpoint (#1506) |
| **V27** — deadlocks database name | `deadlocks.database_name` (the Azure SQL DB per-database deadlock-capture watermark key, #1535) and a refreshed `v_deadlocks` |
| **V28** — Query Store replica role | `query_store_stats.replica_role` (SQL Server 2022+ AG secondary-replica attribution, #1546) and a refreshed `v_query_store_stats` |
| **V29** — long-query completions collector | `collect.long_query_completions` + its index — the opt-in long-running-query completion trace's store table (#1496) |
| **V30** — web dashboard config | `config_service.web_enabled` + `web_port` — the read-only web dashboard's live enable/port toggle, the twin of `mcp_enabled`/`mcp_port` (#1562) |

All timestamps in the store are **naive-UTC** `timestamp` columns — the product-wide cross-store contract (Lite's DuckDB does the same).

### TimescaleDB (Optional, Auto-Adopted)

At startup, right after migration, the service attempts `CREATE EXTENSION IF NOT EXISTS timescaledb` and checks `pg_extension`:

- **Present** — every collector table is converted to a hypertable (partitioned on its own time column into **1-day chunks**, existing rows migrated) and gets a compression policy: chunks older than **1 day** compress automatically (segmented by `server_id`). The short intervals matter at the 1-minute collection cadence — a chunk cannot compress until it closes and then ages, so TimescaleDB's 7-day default left the store fully uncompressed for ~2 weeks (a near-idle 5-server fleet still reached ~1 GB in a couple of days); 1-day chunks + 1-day compress keep it compact (measured ~16.7x on perfmon, ~6.4x on the plan-XML-heavy query_stats). Compressed chunks stay fully queryable — this is Darling's archival tier, the centralized-store answer to Lite's Parquet archive. Everything is idempotent and re-converges on every service start; a table that fails conversion stays a plain table and keeps working.
- **Absent** — the service logs one Information line and runs in plain-PostgreSQL mode, which is a fully supported configuration, not a degraded one.

`IF NOT EXISTS` short-circuits before privilege checks, so a store whose administrator pre-created the extension works for a service login that could never create it.

### Retention

A purge runs on the first sweep after startup and then daily, driven by the same shared per-collector horizons Lite uses:

| Horizon | Tables |
|---|---|
| 7 days | `query_snapshots`, `waiting_tasks`, `running_jobs` |
| 30 days | Most collector tables (wait/query/procedure/Query Store stats, CPU, memory, file I/O, tempdb, perfmon, deadlocks, blocking, sessions, config snapshots), plus `collection_log` and `analysis_findings` |
| 90 days | `database_size_stats`, `index_object_stats` |
| 365 days | `server_properties` |

On plain PostgreSQL the purge is DELETE-based. With TimescaleDB it switches to `drop_chunks` — a metadata-only detach of whole expired chunks (rows inside a partially-expired chunk survive until the whole chunk ages out; up to ~1 day of grace at the 1-day chunk width), with a per-table DELETE fallback for any table that is not a hypertable. Failure-isolated per table: one stuck purge is logged and retried the next day without stopping the sweep.

### Logs

The service's PRIMARY log is a **rolling file** under `%ProgramData%\PerformanceMonitorDarling\logs\darling-service_yyyyMMdd.log` — every collector run line, connect edge, reload notice, warning, and error lands there (buffered writes, one file per day, 14-day retention, and a logging failure can never crash the service). Console runs write the same file plus console output.

Warnings and errors also go to the **Windows Application event log** (source `PerformanceMonitor Darling`) — but only if that event source exists. Registering an event source requires elevation, and the recommended `NT SERVICE` virtual account cannot do it, so run the `New-EventLog` line in the install steps above (or any elevated run of the exe) once; without it, Windows silently drops the events and the file log is your only surface. Collection outcomes are also queryable in the store itself — `collection_log` records every collector run per server with status and timings, and the viewer's Collection Health tab renders exactly that.

### The Viewer

`PerformanceMonitor.Darling.Viewer.exe` is a WPF app that talks **only to the PostgreSQL store** — it never connects to your monitored SQL Servers. It reads the same `darling.json` the service uses, but only the `postgres` section, resolved in the same order (explicit path, then `DARLING_CONFIG`, then `darling.json` next to the binary) plus one viewer-only fallback: the parent directory, so the release zip's layout — viewer in a `viewer\` subfolder, `darling.json` beside the service exe — works with no setup. A viewer seat on another machine needs only a minimal `darling.json` containing the `postgres.connectionString`. If the file is missing it shows a hint instead of crashing.

The layout mirrors the Lite desktop app: a left sidebar lists the servers from the `servers` registry the service maintains, and the top tab strip holds three fixed **aggregate tabs** — Overview, Recommendations, and Alerts — alongside a closable **per-server tab** for each server you open. Overview (the all-servers server-cards grid) and Alerts (the all-servers alert history) span every server; Recommendations has its own server selector, independent of the sidebar. **Double-click a server** in the sidebar — or **double-click its Overview card** — to open (or focus) its tab, and close it with the × on the tab header; an empty-state panel is shown until the store has at least one server.

Each per-server tab has fourteen inner tabs:

| Inner tab | Contents |
|---|---|
| **Overview** | Five correlated, X-axis-synced timeline lanes over the last 24 hours — CPU % (SQL Server vs SQL+other Total), total wait ms/sec, blocking + deadlocking, buffer pool MB, and file-I/O latency — each with a ±2σ baseline band and anomaly markers, all sharing one crosshair so a spike in one lane lines up against the others |
| **Wait Stats** | A searchable wait-type picker (poison + usual-suspect + `PAGELATCH_` defaults, checked-to-top, a 30-type selection guide) beside a per-**type** trend chart for the checked types over the last 24 hours, with a Wait Time (ms/sec) ↔ Avg Wait Time (ms/wait) metric toggle — the per-type companion to the Overview's single total-wait lane |
| **Queries** | Six sub-tabs over the last 24 hours — **Performance Trends** (a 2×2 of per-second trend charts: query duration, procedure duration, Query Store duration, execution count), **Active Queries** (the ~26-column filterable snapshot grid of captured running queries with a time-range slicer, a **Latest Snapshot** button that re-reads the newest stored capture, and per-row Estimated / Actual plan buttons that open the stored plan in the Plan Viewer), **Top Queries by Duration** (the full query-stats grid with in-grid bar cells for executions/CPU/duration/reads and a CPU-by-database breakdown), **Top Procedures by Duration**, **Query Store by Duration**, and **Query Heatmap** (query counts per 5-minute bin × per-execution magnitude bucket, by a chosen metric; right-click a cell to drill into Active Queries for that window) — the three grids each carry a time-range slicer (drag to narrow the window) and a shared **Compare** control that overlays the current window against a baseline period (yesterday, last week, or same day last week), flagging new and vanished queries |
| **Plan Viewer** | Hosts execution plans as closable sub-tabs (the shared plan-viewer control, the same one Lite and the Dashboard use). Right-click a **Top Queries** or **Query Store** row and choose **View Plan** to open the plan the service captured for it (`query_stats.query_plan_xml` / `query_store_stats.query_plan_text`); Top Queries rows also carry a **Query Plan** column whose Download button saves the stored plan as a `.sqlplan` file (enabled only when a plan was captured). Top Procedures and the blocking / deadlock reports deliberately do **not** surface a plan here — procedure plans aren't stored, and blocked-process / deadlock rows carry only a `sql_handle` (not plan XML); resolving either to a plan needs a live SQL connection the viewer never makes. "Get Actual Plan" (a live re-execution) is likewise out |
| **CPU** | Raw per-sample CPU utilization (SQL Server vs other processes) over the last 24 hours — every ring-buffer sample, full-bleed as two series; the Overview's CPU lane plots the same raw samples compactly (SQL vs SQL+other Total) with a baseline |
| **Memory** | Four sub-tabs over the last 24 hours — **Overview** (a summary strip of physical / SQL Server / target / buffer pool / plan cache / page-file memory plus the system memory state and model, over a Total-vs-Target-vs-Buffer-Pool memory trend with a memory-grants overlay), **Memory Clerks** (a searchable clerk-type picker — top-5 default, checked-to-top, clear-only-the-filtered — beside a per-clerk memory trend for the checked clerks with a non-buffer-pool total and top-clerk summary), **Memory Grants** (per-resource-pool grant sizing — available / granted / used MB — and activity — grantees / waiters / timeouts / forced grants), and **Memory Pressure Events** (hour-bucketed stacked bars of `RING_BUFFER_RESOURCE_MONITOR` pressure, SQL Server vs OS, medium vs severe) |
| **File I/O** | Two sub-tabs over the last 24 hours — **Latency** (per-file read and write latency, with a dashed queued-I/O overlay) and **Throughput** (per-file read and write MB/s) — the top 10 files by activity |
| **tempdb** | Three stacked charts over the last 24 hours — space usage (user / internal objects / version store), total allocated size, and per-file I/O latency |
| **Blocking** | Four sub-tabs over the last 24 hours — **Trends** (lock-wait rate, blocking incidents, deadlocks), **Current Waits** (waiting-task duration by wait type, blocked sessions by database), **Blocked Process Reports** (the full ~25-column filterable grid — XE reports preferred with the always-on DMV blocking snapshot merged in as fallback, each row badged with its source, a time-range slicer, per-row report-XML save, and long-block highlighting; double-click or right-click **View Block Chain** to reconstruct and draw the blocking chain the row belongs to), and **Deadlocks** (one filterable row per process parsed from each deadlock graph, a slicer, per-row graph-XML save; double-click or right-click **View Deadlock Graph** to draw the deadlock graph) |
| **Perfmon** | A searchable counter picker with the shared counter packs (General Throughput, Memory Pressure, CPU / Compilation, I/O Pressure, TempDB Pressure, Lock / Blocking) beside a per-counter delta trend for the checked counters (up to 12) over the last 24 hours |
| **Running Jobs** | Latest snapshot of currently-running SQL Agent jobs — start time, current vs average vs p95 duration, % of average, and a highlighted row when a job is running past its p95 (a store-derived banner appears when the service's login lacks msdb access) |
| **Configuration** | Four column-filterable snapshot grids of the server's latest capture — server configuration (`sys.configurations`), database configuration (28 columns of `sys.databases`), database-scoped configuration, and trace flags |
| **Daily Summary** | A one-row roll-up of the selected day (default today, UTC, with a date picker) — total wait time, the top wait type, distinct query count, deadlock / blocking-event / high-CPU-sample counts, collector errors, and an overall health band |
| **Collection Health** | Three sub-tabs — **Health Summary** (a 7-day per-collector roll-up: run / success / error counts, failure rate, average duration, last success / run / error, and a health band of HEALTHY / WARNING / STALE / FAILING / NEVER_RUN / NO_PERMISSIONS — double-click a collector to open its full run history), **Collection Log** (the recent run log with per-run SQL and store-write timings and row counts), and **Duration Trends** (a per-collector success-duration scatter) |

The three aggregate tabs — **Overview** and **Alerts** span every server; **Recommendations** has its own server selector, independent of the sidebar:

| Tab | Contents |
|---|---|
| **Overview** | A card per registered server (all servers, not the sidebar selection): server name + status dot, CPU (total non-idle with the SQL-only number alongside), memory, blocking and deadlock counts over the last hour, and last-collection time, each colour-banded (CPU ≥ 80% red / ≥ 50% amber / green; blocking and deadlocks red-or-amber when present) with a red **Offline** overlay. Status is derived from **collection freshness** — the newest `collection_log` age — rather than a live ping (the viewer never connects to the monitored servers): fresh is Online, older than twice the fastest collector's one-minute cadence is a Warning, and no recent collection is Offline. **Double-click a card** to open that server's tab. Refreshes every 30 seconds |
| **Recommendations** | The latest analysis run's findings for the tab's **own selected server** — a server selector independent of the sidebar, a Refresh button, and a status line showing the last analysis time — re-skinned to Lite's advise-only **card** design: a scrollable list of collapsible **incident** sections, each holding severity-banded cards (a severity badge, the affected `[database]`, the title, and the advice). Every card offers **Ask AI** (copies an MCP investigation prompt referencing `analyze_server` / `get_analysis_findings`); a card whose stored remediation carries a copy-paste statement also offers **Copy fix** (copies the suggested T-SQL). Advise-only — the viewer never applies anything, and there is no mute affordance here (alert muting lives on the Alerts surface). There is no in-app "Generate now": the service runs analysis on its own 30-minute cadence, so the status line surfaces the last analysis time instead |
| **Alerts** | The full alert history from `config_alert_log` across **all servers** (newest first, selectable time range), with a Server column and a Server filter. Double-click a row (or **View Details**) for a modal detail window showing the alert's stored detail and structured advice / remediation / drill-down from its dedup-fingerprint context. **Dismiss Selected / Dismiss All** hide alerts from the view (a durable `dismissed` flag on `config_alert_log`); column filters, Copy Cell/Row/All, and Export to CSV match Lite's grid. Right-click to **Mute This Alert** or **Mute Similar** (metric-only), and a **Manage Mute Rules** button opens the mute-rule editor |

Only the visible tab loads (Lite's visible-only rule). The Alerts tab and the visible server tab's active inner tab refresh every 60 seconds; the Overview refreshes on its own faster 30-second timer (Lite's Overview cadence); and **Recommendations** refreshes on tab activation, its Refresh button, and its own server-selector change only, never on the timer — its findings change on the service's 30-minute analysis cadence, so a 60-second auto-refresh would be pointless churn (and would reset the incident expanders under the reader), matching Lite.

The viewer is read-only over collected data, but it does perform a small set of **user-initiated writes** — and those go straight to the PostgreSQL store, which is the coordination point (the service honors them on its next read; there is no viewer-to-service channel). From the Alerts tab, creating a mute rule from an alert (**Mute This Alert** / **Mute Similar**) or adding, editing, toggling, deleting, or purging one via **Manage Mute Rules** writes `config_mute_rules` (a rule scopes to a server by name, exactly as Lite's mute rules do); and **dismissing alerts** sets the `dismissed` flag on `config_alert_log` so they drop out of the Alert History view (a single atomic UPDATE — Darling has no parquet archive tier, so there is no dismissed-archive sidecar). The viewer never writes collector data.

### Restart Semantics

The service is built to restart cleanly, any time:

- **Delta continuity** — delta-based collectors (wait stats, file I/O, perfmon, memory grants) re-seed their baselines from the store at startup, so the first cycle after a restart produces real deltas instead of zeroes.
- **Alert no-re-fire** — edge-trigger watermarks and the failed-job watermark persist in `config_edge_trigger_watermarks`, and per-alert cooldowns re-seed from `config_alert_log`, so a restart does not replay alerts you already received.
- **Idempotent store setup** — migrations are versioned and skip what is already applied; TimescaleDB conversion and compression policies re-converge as no-ops.
- **Per-connect snapshots** — the on-connect config snapshot collectors run once per (re)connect, mirroring Lite's server-open behavior.
- Mute rules (`config_mute_rules`) load once at service startup — restart the service after adding rows.

A monitored server that is down is retried every 60 seconds forever; a collector that errors is logged and retried at its next scheduled time; a mid-cycle connection-level failure forces a clean reconnect and re-probe. The loop never dies for one bad cycle.

---

## Troubleshooting

**"Cannot load configuration"** (critical, service idles) — no `darling.json` was found at the resolved path. The message names the path it tried; copy `darling.sample.json` there or point `DARLING_CONFIG` at your file.

**"Configuration problem: ..."** (critical, service idles) — validation failed. The messages are literal and per-field, e.g. `postgres.connectionString is required.`, `servers must contain at least one entry.`, `server 'X': host is required.`, `server 'X': sql auth requires username.`, `server 'X': sql auth requires encryptedPassword (preferred; see --encrypt-password) or password.`, `server 'X': auth must be 'integrated' or 'sql'`. Fix the file and restart the service.

**"Cannot reach or migrate the Postgres store"** (critical, service idles) — the store connection string is wrong, PostgreSQL is down/unreachable, or the login cannot create tables. Collection does not start until this succeeds; fix and restart.

**"uses a plaintext password in darling.json"** (warning, every connect) — you set `"password"` instead of `"encryptedPassword"`. It works, but run `--encrypt-password` on the service machine and switch.

**DPAPI decrypt fails after moving darling.json** — `encryptedPassword` blobs are machine-bound (DPAPI LocalMachine). Re-run `--encrypt-password` on the new machine.

**"Failed to ensure XE sessions"** — the login lacks `ALTER ANY EVENT SESSION` (or the database-scoped equivalent on Azure SQL Database). Deadlock and blocked-process collection read zero rows until the sessions exist; grant the permission or have an administrator create/start `PerformanceMonitor_Deadlock` and `PerformanceMonitor_BlockedProcess`. "Already exists / already started" XE errors are logged as benign and mean the sessions are up.

**Blocked-process reports empty** — the blocked-process threshold may still be 0. On AWS RDS set `blocked process threshold (s)` via a Parameter Group (the `sp_configure` bootstrap cannot run there); on Azure SQL Database the threshold is fixed at 20 seconds. Blocking stays visible either way through the always-on DMV blocking snapshot.

**`PERMISSIONS` rows in `collection_log`** — that collector's reads were denied (SQL errors 229/297/300). Check the [permissions](#permissions-on-monitored-servers); the collector retries every cycle and recovers as soon as the grant lands.

**"Skipping recently-failed-job check"** (info) — the login has no msdb / `SQLAgentReaderRole` access, so failed-job alerts are skipped. Expected for minimal-privilege monitoring logins; grant the role if you want job alerts.

**"TimescaleDB setup failed — continuing in plain-PostgreSQL mode"** (warning) — the extension exists but conversion hit a problem. Everything still works (DELETE-based retention, plain tables); conversion is retried on the next service start.

**"Why are there 40+ postgres.exe processes?"** — the count is three populations, and only one is client connections: (1) PostgreSQL's own system processes (postmaster, checkpointer, WAL/background writers, autovacuum, stats); (2) **TimescaleDB background workers** — the managed conf sizes `timescaledb.max_background_workers` to the hypertable count + 2 (≈38), and every RUNNING compression/retention policy job is its own process, so the count legitimately surges during checkpoint/compression waves and falls back when they finish; (3) client backends — the service's pools are capped at 24, the co-located viewer's at 10. Decompose it live with: `SELECT backend_type, count(*) FROM pg_stat_activity GROUP BY backend_type ORDER BY 2 DESC;` — and remember Windows charges the shared buffer segment to every attached process's working set, so per-process memory numbers cannot be summed.

**query_store bursts every ~15 minutes** — two or three near-empty cycles, then one large one, is Query Store's own behavior, not a collector bug: the engine buffers in memory and flushes to its persisted tables on `DATA_FLUSH_INTERVAL_SECONDS` (default 900s), so the collector genuinely sees nothing new between flushes. Narrowing the collection interval will not smooth it. The per-database log lines show which database drove a burst.

**MCP client cannot connect** — MCP defaults to off. Enable it live from the Viewer's Settings (the checkbox writes the control plane; the service starts the endpoint within seconds, no restart), or set `mcp.enabled: true` in `darling.json` for a file-seeded install. If the log says `Port 5152 is already in use — MCP server not started`, change `mcp.port`. The MCP server binds to `localhost` only unless you opt into a LAN endpoint (see [Opt-in Network Endpoints (LAN)](#opt-in-network-endpoints-lan)); a remote client that gets 401 is missing or mismatching the required bearer token, and one that is refused before any response is outside the configured `allowFrom` CIDR.

**Recommendations tab says no findings** — analysis runs every 30 minutes per server but only once the store holds at least 24 hours of collected data for that server; a fresh install simply has not earned findings yet.

---

## How It Runs (Reference)

Fixed cadences, hardcoded on purpose:

| What | Cadence |
|---|---|
| Collector sweep loop | Every 15 seconds (each collector runs when its own shared schedule is due — most every 1 minute, some every 5, sizes hourly, index stats daily) |
| Alert evaluation | Every 30 seconds per connected server (Lite's overview cadence) |
| Scheduled analysis | Every 30 minutes per server, 120-second budget, analyzing the last 4 hours; findings persist to `analysis_findings` and high-severity ones notify through the configured channels |
| Retention purge | First sweep after startup, then daily |
| Reconnect attempts | Every 60 seconds while a server is unreachable |

---

## Managed Bundled PostgreSQL

With `postgres.managed = true` (the sample's default), the service runs its own bundled PostgreSQL 18 + TimescaleDB and a from-zero install needs no database provisioning at all. Windows only, like every DPAPI surface here.

```json
{
  "postgres": {
    "managed": true,
    "port": 5641,
    "dataDirectory": null
  }
}
```

**What first run does.** The service looks for `pg-runtime\pgsql\` beside its binary, extracting it from `pg-runtime.zip` when only the zip is present (deleting the extracted directory is therefore always safe — it self-heals). If the data directory has no cluster, it generates a 32-character random password, protects it with DPAPI LocalMachine into `pg-credential.dpapi` beside the data directory (credential first, so a crash mid-initdb never strands a cluster nobody can log into), then runs `initdb` with `scram-sha-256` auth, data checksums, and UTF8/C locale. A marker-guarded block appended to `postgresql.conf` preloads TimescaleDB, sets the port, and restricts listening to `127.0.0.1`; a second versioned block sizes background workers up for the per-hypertable compression jobs (`timescaledb.max_background_workers = 28`, `max_worker_processes = 40` — PostgreSQL's default of 8 workers cannot launch them); a third versioned block sizes memory from the host's physical RAM for the up-to-500-servers case (`shared_buffers = min(25% RAM, 8GB)`, `effective_cache_size = 75% RAM`, `maintenance_work_mem = min(5% RAM, 1GB)`, and a deliberately-modest per-connection `work_mem = clamp(RAM/512, 16MB, 64MB)` — on an 8 GB box that is `shared_buffers 2048MB` / `work_mem 16MB`; the stock 128 MB / 4 MB defaults are fine at small scale but bottleneck at fleet scale). All three appends are re-checked on every start, so a crash between initdb and the append heals itself instead of silently degrading — and clusters initialized before the worker or memory sizing existed gain the missing block on their next start (effective at the next PostgreSQL restart). Then `pg_ctl start`, `CREATE DATABASE darling`, and the normal startup path (migrations, TimescaleDB adoption — you should see `32/32 collector table(s) are hypertables`) continues exactly as in bring-your-own mode. The connection string is derived from the stored credential; the Viewer and the MCP host on the same machine derive it the same way, so nothing needs configuring there either.

**Why scram and not trust, even loopback-only.** Trust auth would hand superuser to any local code that can open a loopback socket — every other local user, and network-capable-but-not-filesystem-capable attack primitives like SSRF from a co-hosted app. With scram the credential travels on the wire, failed attempts are auditable, and access is confined to what can read the DPAPI-protected credential file. `listen_addresses = '127.0.0.1'` keeps the server unreachable off the machine on top — unless you deliberately opt into a LAN endpoint (see [Opt-in Network Endpoints (LAN)](#opt-in-network-endpoints-lan)), which reconciles `listen_addresses`, a `hostssl` pg_hba rule, and TLS on every start and is otherwise off.

**Lifecycle.** On shutdown the service stops the server (`pg_ctl stop -m fast`) **only when it started it**. A server that was already running — an operator's own `pg_ctl`, or a postmaster that survived a service crash — is adopted for connections but never stopped: you'll see `already running … will not stop it` in the log, and the service keeps collecting into it.

**The runtime zip.** `pg-runtime.zip` ships beside the service binary in packaged releases. Building from source, produce it once with `Darling\tools\fetch-pg-runtime.ps1` — it downloads the pinned EDB PostgreSQL 18 binaries and TimescaleDB, verifies their SHA256, prunes what the service doesn't need, and writes the zip to `Darling\artifacts\`; copy it next to the built service exe.

**Server log.** The bundled server's own log is `pg.log` beside the data directory — that's where PostgreSQL explains a refused start; bootstrap errors in the service log quote its tail.

## Security & Least-Privilege Roles

The store is split into two schemas so that no consumer connects with more privilege than it needs:

- **`collect`** — the 32 collector hypertables plus the service-written, user-read metadata (`servers`, `collection_log`, `analysis_findings`, the `v_*` views). Read-only to everyone but the service.
- **`config`** — exactly the tables a human operator changes through the Viewer or MCP: `config_mute_rules`, `config_alert_log` (alert dismissals), `config_edge_trigger_watermarks`, and `analysis_muted`.

Table names are unchanged — only their schema moved — and the shared SQL keeps using the bare, unqualified names, resolved through `search_path = collect, config, public` (set as the database default and carried on the managed connection strings). This is deliberate: Darling's SQL is byte-identical to Lite's DuckDB SQL, and re-qualifying it would fork that twin.

**The roles.** The service still owns the store as the `darling` superuser (it does the DDL — migrations, hypertable conversion, retention). On top of that, **managed mode provisions three least-privilege login roles** (BYO provisions two — see below):

| Role | Privileges | Used by |
|---|---|---|
| `darling` | superuser / owner | the service (collection, migration, provisioning) |
| `admin` | SELECT on both schemas + INSERT/UPDATE/DELETE on `config` only | the Viewer, by default (`connectAs: "admin"`) |
| `viewer` | SELECT on both schemas + INSERT/UPDATE/DELETE on `config.custom_views` only (the web composer's saved views) | a locked-down Viewer (`connectAs: "viewer"`) |
| `mcp` | `viewer`'s exact read surface + INSERT on `collect.analysis_findings` / `config.analysis_muted` + INSERT/UPDATE/DELETE on `config.custom_views` (the custom-view tools) + the alert-tuning writes (INSERT/UPDATE/DELETE on `config.config_mute_rules`, UPDATE on `config.config_alert_settings`, and the `config_service` reload-beacon columns) + the server-onboarding writes (INSERT/UPDATE/DELETE on `config.config_monitored_servers` — the credential column stays SELECT-carved, so it can WRITE a password blob but never READ one back) | the store identity the opt-in MCP **network** endpoint connects as (managed only); dormant until MCP is exposed on the LAN |

`admin` cannot `DROP`, alter schema, touch `collect` data, or create objects — it can only do what the Viewer's mute-rule / alert-dismiss surfaces need. The `mcp` role is narrower still: it reads exactly what `viewer` reads (the secret config columns are carved out identically) and its writes are a small, enumerated set — the two analysis-table INSERTs (`analyze_server` + `mute_analysis_finding`), the single-table `config.custom_views` CRUD (the custom-view tools), the alert-tuning writes (`config.config_mute_rules` CRUD + a single-row `config.config_alert_settings` UPDATE, plus the two `config_service` beacon columns so a settings write's self-bump trigger can fire), and the server-onboarding writes (`config.config_monitored_servers` CRUD for `add_servers` / `remove_server` — its `config_monitored_servers` write fires the SAME `config_service` beacon trigger, already covered by that column grant) — so a token-holder on the network MCP endpoint can never reach the `config`-table service-credential pivot, the secret columns, or a service flag like `paused`. Even on `config_monitored_servers`, which it may write, the `encrypted_password` column stays in the fail-closed secret carve, so `mcp` can WRITE a credential blob (onboarding) but can never READ one back. `ALTER DEFAULT PRIVILEGES` means new collector tables auto-inherit SELECT for `admin`/`viewer`, so the model never drifts as collectors are added (every `mcp` write is an explicit single-table/single-column grant, deliberately not schema-wide).

**Managed mode** provisions all of this automatically on every start (idempotent and self-healing), generating a per-role DPAPI-LocalMachine credential — `pg-admin-credential.dpapi`, `pg-viewer-credential.dpapi`, and `pg-mcp-credential.dpapi` beside the data directory, same posture as the owner's `pg-credential.dpapi`. Nothing to configure beyond `connectAs`.

**Credential file protection.** DPAPI LocalMachine scope is deliberate (the service writes the credential, a *different* interactive user's Viewer reads it), which means the machine-bound blob is decryptable by anything that can *read* the file. So the credential files are locked down with an NTFS ACL that strips the inherited world-read `%ProgramData%` would give them:

| File(s) | Readable by |
|---|---|
| `pg-credential.dpapi` (superuser), `pg-mcp-credential.dpapi` (the network MCP role) + the transient init pwfile | SYSTEM, Administrators, the service account — **not** interactive users |
| `pg-admin-credential.dpapi`, `pg-viewer-credential.dpapi` | the above **+ `NT AUTHORITY\INTERACTIVE`** (the operator's Viewer) |

`pg-mcp-credential.dpapi` sits with the superuser (non-interactive) rather than with the Viewer's credentials because only the in-service MCP host reads it — never an interactive Viewer.

The principal model assumes the **single-operator VM** this edition targets: `INTERACTIVE` == the operator, so the admin/viewer credentials are readable by the Viewer with zero configuration, while non-interactive local code (other services, sandboxed/SSRF socket primitives, scheduled tasks) and the superuser credential are excluded outright. On a shared machine where untrusted users log on interactively, tighten those two files to the specific operator account by hand. The service also refuses to trust a credential file that isn't owned by SYSTEM/Administrators/itself (closing a pre-plant attack), and regenerates an untrusted role credential.

**A read-only (`viewer`) Viewer degrades gracefully.** It probes its own privileges on connect (`has_table_privilege`), so the mute-rule Add/Edit/Toggle/Delete/Purge buttons and the alert Dismiss / Dismiss All buttons are hidden or disabled, and any write that still slips through returns a clear "read-only connection" message instead of an error.

**Bring-your-own PostgreSQL.** The schema split runs everywhere (it's a migration — the service applies it on startup and best-effort sets the database `search_path`; if your collection login can't `ALTER DATABASE`, run that one statement yourself as the owner). Role provisioning is managed-only, so for BYO you create the roles yourself, once, with the shipped script:

```
psql -h <host> -U <owner> -d darling -f Darling/tools/provision-roles.sql
```

Edit the two password placeholders (and the database/owner names if yours differ) first. Then point a read-only Viewer's `connectionString` at the `viewer` role. **`provision-roles.sql` creates two login roles — `admin` and `viewer`** — the two the Viewer connects as. Managed mode creates a third, `mcp`, but BYO deliberately does not: the MCP **network** endpoint (the only consumer of the `mcp` role) is managed-mode-only, and a BYO operator governs their own PostgreSQL's network exposure. If you expose MCP through your own reverse proxy against a BYO store, point it at whichever least-privilege role you choose (the `viewer` role covers the read tools; `analyze_server`'s finding persistence and `mute_analysis_finding` need INSERT on `collect.analysis_findings` / `config.analysis_muted`).

## Opt-in Network Endpoints (LAN)

By default all three network surfaces bind **loopback only** — the store to `127.0.0.1`, the MCP server and the web dashboard to `localhost` — exactly as they always have. Three optional, independent opt-ins let a remote viewer, MCP client, or browser on your **trusted LAN** reach them. This is a home-lab / trusted-subnet feature: **never expose any of these endpoints to the internet.** All three are **managed-mode only** (in bring-your-own mode your own PostgreSQL / reverse proxy governs exposure, and the config is ignored with a warning), and all three are **fail-closed** — any invalid or incomplete field degrades that endpoint back to loopback and logs a critical line rather than exposing it. Removing the config on the next restart closes the box again.

### Guided setup (`--configure-network`)

The fastest path is the interactive wizard — run it on the **service host**:

```
PerformanceMonitor.Darling.Service.exe --configure-network
```

It shows the current exposure (read from the service's own resolvers), then walks you through the **store**, **MCP**, the **web dashboard**, any comma combination (e.g. `1,3`), or all three at once (or a **disable** that removes all exposure). Every answer is validated **by delegation to the exact checks the running service fail-closes on**, so the wizard can never write a config the service would refuse — it re-prompts with the resolver's own reason. It generates the MCP bearer / web access tokens for you (DPAPI-protected; each plaintext is printed once, so save it then), edits `darling.json` **in place preserving every comment** behind a timestamped `darling.json.bak-<timestamp>` backup, prints the scoped firewall command(s), the `--print-viewer-connection` handoff, and the web dashboard's browser login URL (`http://<listen>:<port>/?token=...`), and offers to restart the service to apply. `install-darling.ps1 -Network` runs it automatically right after the install reaches Running. The manual field reference below documents exactly what it writes.

### Headless enable/disable + firewall (`--enable-mcp` / `--enable-web`)

On a box with no Viewer, two things are otherwise awkward: the `enabled` flags in the `mcp` / `web` blocks below are only a **first-run seed** — after the first run the store (`config.config_service.mcp_enabled` / `web_enabled`) is authoritative and is normally flipped only from the Viewer's Settings — and the service account (`NT SERVICE\PerformanceMonitor Darling`) **cannot open the firewall itself**. Four verbs, run on the **service host**, close both in one elevated action:

```
PerformanceMonitor.Darling.Service.exe --enable-mcp
PerformanceMonitor.Darling.Service.exe --disable-mcp
PerformanceMonitor.Darling.Service.exe --enable-web
PerformanceMonitor.Darling.Service.exe --disable-web
```

Each flips only its endpoint's **live store flag** with a targeted `config_service` write; the service **hot-reloads within one collection sweep — no restart.** If that endpoint's `network` block opts into LAN exposure (a non-loopback `listen`), the verb also reconciles the **same scoped, idempotent-by-name firewall rule the service would**: **run elevated**, it opens (or, on `--disable-*`, removes) the rule; **run non-elevated**, the store toggle still succeeds and it prints the exact elevated firewall command to run by hand (a loopback-only endpoint needs no rule and says so). Managed-mode only, Windows only. So the headless bring-up is: write the `network` block (the wizard above or the manual reference below), then `--enable-mcp` / `--enable-web` from an **elevated** shell.

### Verify it's actually reachable (and the two failures that look like bugs)

Enabling an endpoint is **not** the same as reaching it, and both common failures leave the store flag reading `true`, so "it says enabled" is not proof. After `--enable-mcp` / `--enable-web`, verify on the **service host**:

1. **The listener is on the LAN address, not loopback.** `Get-NetTCPConnection -State Listen | Where-Object LocalPort -eq 5152` (or `5153` for web) must show the box's LAN IP, e.g. `10.0.0.5:5152` — **not** only `::1` / `127.0.0.1`. *Enabled but still loopback-bound* is the single most common failure: the store flag is on, but the service loaded `darling.json` **before** the `network` block existed. The block is read **once at service start** — the enable toggle stops/starts the endpoint with the already-loaded config and does **not** reload the file. **Restart the service** (`Restart-Service 'PerformanceMonitor Darling'`) so it re-reads the block, then re-check the listener; after the restart run `--enable-mcp` **elevated** only if the firewall rule is missing (the service self-reconciles the rule best-effort, but its service account cannot open the firewall itself).
2. **The scoped firewall rule exists and covers the client.** `Get-NetFirewallRule -DisplayName 'PerformanceMonitor Darling MCP (port 5152)'` (or `... Web (port 5153)`) should be `Enabled=True, Action=Allow`, scoped to the `network.allowFrom` CIDR. A non-elevated `--enable-*` skips this and prints the exact command to run by hand.

Then from the **client** host:

3. **Connect to the box's LAN IP, never `localhost`.** Use `http://<box-LAN-IP>:5152/`. `localhost` / `127.0.0.1` only resolves *on the box itself*, so an off-box MCP client pointed at localhost fails silently — this is the number-one "MCP won't connect" cause. Send `Authorization: Bearer <token>` (the `network.token`), and do a **fresh** `initialize` + `tools/list` rather than trusting a cached tool list from a previous version.

**After a reinstall:** the installer replaces binaries but does **not** touch `darling.json` (the zip ships only `darling.sample.json`) or the store, so the `network` block and both live flags survive the upgrade — and the reinstall restarts the service, which re-reads the block. If MCP stops connecting afterward it is almost always failure 3 (the client pointed at `localhost`) or a missing firewall rule, **not** lost config: run `--enable-mcp` **elevated** to re-open the rule if check 2 comes up empty. A stale loopback bind is unlikely after a restart unless the block itself is invalid, in which case the endpoint fail-closes to loopback and logs a critical line saying why — fix the block and restart again. A full `--configure-network` re-run is only needed if the `network` block itself is gone.

### Store endpoint (viewer over the LAN)

Add a `network` block to `postgres` (managed mode):

```json
"postgres": {
  "managed": true,
  "port": 5641,
  "network": {
    "listen": "192.168.1.205",
    "allowFrom": "192.168.1.0/24",
    "role": "viewer"
  }
}
```

On every start the service reconciles this against the live cluster: it adds the bind IP to `listen_addresses`, generates a self-signed TLS certificate (`server.crt` / `server.key` beside the data directory, with both an IP SAN for `listen` and a DNS SAN for the machine hostname), writes a marked `hostssl darling <role> <allowFrom> scram-sha-256` rule into `pg_hba.conf` and reloads, and best-effort adds a firewall rule.

- **`role`** — the pg_hba login role the rule names: `"viewer"` (default, **read-only** — the secure default, covering a laptop reading every dashboard, chart, and finding) or `"admin"` (full remote **writes**; the service logs a warning because `admin` holds the `config_command` / `config_monitored_servers` / `config_notification` service-credential pivot). Never the superuser. This is **distinct from `postgres.connectAs`** (the *local* VM viewer's loopback role, default `admin`): `network.role` is the *remote* role and defaults to `viewer`, so the two have opposite defaults — the local seat is writable, the remote seat is read-only, unless you say otherwise.
- **TLS is verify-full, not `require`.** Because Darling generates the cert, the client can pin it, so the connection string below uses `SSL Mode=VerifyFull` — which actually defends against an on-path MITM (`require` verifies nothing). The store's network pg_hba line is `hostssl`, so a non-TLS network client is refused.
- **The firewall is defense-in-depth, and you should set the scoped rule yourself** (the service's best-effort rule is a convenience). The store's real boundary is pg_hba + TLS:

  ```
  New-NetFirewallRule -DisplayName "Darling store (Postgres)" -Direction Inbound -Action Allow -Protocol TCP -LocalPort 5641 -RemoteAddress 192.168.1.0/24
  ```

**Remote-viewer handoff.** On the **service host**, run:

```
PerformanceMonitor.Darling.Service.exe --print-viewer-connection
```

It decrypts the `network.role` credential and prints a paste-ready connection string plus the server certificate PEM. It prints a **live database password to STDOUT** — redirect it to an ACL'd file or pipe it to the clipboard (`... --print-viewer-connection | clip`); do not leave it in shell scrollback, CI logs, or a screenshare. On the **viewer machine**, set a minimal `darling.json` to bring-your-own mode and paste the string in verbatim (no viewer code path changes — the string is consumed as-is), then save the emitted PEM where `Root Certificate` points:

```json
{
  "postgres": {
    "managed": false,
    "connectionString": "Host=192.168.1.205;Port=5641;Username=viewer;Password=...;Database=darling;Search Path=collect,config,public;SSL Mode=VerifyFull;Root Certificate=server.crt"
  }
}
```

- **Certificate placement + rotation.** Save the emitted PEM at the `Root Certificate` path on the viewer machine (a bare `server.crt` resolves beside the viewer's working directory; an absolute path also works). The cert **auto-regenerates if the bind IP changes** (so verify-full keeps working after a `listen` change) — when that happens, clients must re-run `--print-viewer-connection` and replace their saved cert. To rotate on demand, **delete `server.crt` and `server.key`** beside the data directory; the service regenerates the pair on its next start.
- **Plaintext at rest on the viewer machine.** The pasted connection string holds the role password in cleartext in the laptop's `darling.json` (there is no client-side secret store yet). That is acceptable for the read-only `viewer` credential on a single-operator, ACL'd profile; if you use `role: "admin"`, treat that file as a secret and NTFS-ACL it to your account. DPAPI-encrypting the viewer's BYO connection string is future hardening, out of scope today.

### MCP endpoint (assistant over the LAN)

Add a `network` block to `mcp` (managed mode; `mcp.enabled` must be `true`):

```json
"mcp": {
  "enabled": true,
  "port": 5152,
  "network": {
    "listen": "192.168.1.205",
    "allowFrom": "192.168.1.0/24",
    "encryptedToken": "<output of --encrypt-password>"
  }
}
```

When `listen` is a network address **and** a token is present **and** `allowFrom` is a valid CIDR, the MCP host binds that interface behind two gates: a **required bearer token** (checked first, constant-time, no loopback exemption) and an **in-app CIDR check** on the remote address (loopback is always allowed, so local clients keep working). Any missing precondition keeps MCP loopback-only. Prefer `encryptedToken` (a DPAPI blob from `--encrypt-password`); a plaintext `token` works for dev but is warned. Set the same scoped firewall rule for the MCP port:

```
New-NetFirewallRule -DisplayName "Darling MCP" -Direction Inbound -Action Allow -Protocol TCP -LocalPort 5152 -RemoteAddress 192.168.1.0/24
```

**Blast radius, stated honestly.** The token gates the entire read surface, `analyze_server` (which opens **live outbound connections to your monitored SQL Servers** — the plan-fetcher), the Custom Views management tools, which **create / modify / delete** the saved dashboards and notebooks in `config.custom_views`, the alert-tuning tools (`update_alert_settings` / `create_mute_rule` / `delete_mute_rule`), which change the shared alert configuration the service delivers on, *and* the server-onboarding tools (`add_servers` / `remove_server`), which **add or remove the monitored servers themselves** in `config.config_monitored_servers` — including storing a SQL-auth **credential** for a server it adds. Treat the token as a high-value secret. The store-side identity is still the least-privilege `mcp` role: read, the two analysis-table INSERTs, INSERT/UPDATE/DELETE on the single `config.custom_views` table (the same narrow write the web composer's `viewer` role has), the narrow alert-config writes (`config.config_mute_rules` CRUD + a single-row `config.config_alert_settings` UPDATE, plus the `config_service` reload-beacon columns), and the single-table `config.config_monitored_servers` CRUD. So a token-holder can read everything collected, trigger analysis, author custom views, tune alerting, and onboard/offboard servers — but can never reach the `config_command` service-credential pivot, the carved secret columns (SMTP/webhook credentials, and the monitored-server `encrypted_password` blob it can WRITE during onboarding but never READ back, all included), or a service flag like `paused`, and a stored view is structurally incapable of reading a config control-plane table (a composed query names only `collect.*` collector tables). Custom-view JSON and alert config carry no secrets.

**A monitored-server credential rides `add_servers` — mind the wire.** `add_servers` accepts a SQL-auth `password` in its request JSON; the service DPAPI-encrypts it at rest (never returns it), but it travels to the MCP endpoint in the clear on the same plaintext HTTP the token does. That is one more reason to **front the MCP port with the TLS reverse proxy below** on any segment you do not fully trust — the password, like the token, is captured by an on-path attacker otherwise. Prefer Windows/integrated auth for onboarded servers where you can (no per-server secret crosses the wire at all).

**MCP has no TLS — the MITM control is a TLS reverse proxy.** A self-signed cert breaks real MCP clients, so the MCP endpoint is plain HTTP and the bearer token travels **cleartext on the segment**; an active on-path attacker (ARP spoof, rogue DHCP, compromised switch) could capture and replay it. The in-app CIDR bounds *who can route to* the port; it does **not** protect the wire. If your segment is not fully trusted, put a **TLS-terminating reverse proxy** in front of the MCP port and point clients at that — the named MITM control for this endpoint. (The store endpoint needs no such proxy: it has verify-full TLS built in.)

### Web endpoint (browser over the LAN)

Add a `network` block to `web` (managed mode; `web.enabled` must be `true`):

```json
"web": {
  "enabled": true,
  "port": 5153,
  "network": {
    "listen": "192.168.1.205",
    "allowFrom": "192.168.1.0/24",
    "encryptedToken": "<output of --encrypt-password>"
  }
}
```

When `listen` is a network address **and** a token is present **and** `allowFrom` is a valid CIDR, the web host binds that interface behind two gates: an **in-app CIDR check** on the remote address and an **access token**. A browser presents the token ONCE via `?token=` (open `http://192.168.1.205:5153/`, then paste it into the minimal login form, or append `?token=...` directly); the host validates it constant-time, sets an **HMAC-signed, HttpOnly, SameSite=Strict session cookie**, and 302-redirects to strip the token from the URL so it never lingers in history or a Referer header. Subsequent requests ride the cookie. **Loopback is always allowed tokenless, even while exposed** — unlike MCP, the read-only dashboard has no loopback-token requirement. An out-of-CIDR request is refused with **403**. The cookie signing key is per-process, so a service restart invalidates open sessions (just re-present the token). Prefer `encryptedToken` (a DPAPI blob from `--encrypt-password`); a plaintext `token` works for dev but is warned. Set the same scoped firewall rule for the web port:

```
New-NetFirewallRule -DisplayName "Darling Web" -Direction Inbound -Action Allow -Protocol TCP -LocalPort 5153 -RemoteAddress 192.168.1.0/24
```

**Blast radius.** The web dashboard is **read-only over the collected store** — it connects as the least-privilege `viewer` role and hosts no write paths and no live-server queries (no `analyze_server`, no plan re-execution), so a token-holder can view everything collected but change nothing and reach no monitored server. That smaller blast radius is why loopback stays tokenless here while MCP's does not.

**Web has no TLS either — same reverse-proxy control as MCP.** The token/cookie travels cleartext on the segment, so the in-app CIDR bounds *who can route to* the port but does not protect the wire. On an untrusted segment, put a TLS-terminating reverse proxy in front of the web port.
