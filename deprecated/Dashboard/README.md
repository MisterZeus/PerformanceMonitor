# Performance Monitor Dashboard (Full Edition)

> **The Full edition is deprecated.** It installs a `PerformanceMonitor` database on the target SQL Server with T-SQL collectors running via SQL Agent, and this WPF app connects to view the data. It still ships and is supported for existing users, but new deployments should use **[Lite](../Lite/README.md)** (portable desktop app, nothing installed on the server) or **[Darling](../Darling/README.md)** (headless service + viewer). See the [root README](../README.md) for the current editions.

The Dashboard connects to SQL Server instances running the `PerformanceMonitor` database and visualizes collected performance data across six tab groups, with a NOC-style landing page of green/yellow/red server health cards, interactive ScottPlot trend charts, and an embedded MCP server. Requires the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/10.0).

## Install

The database is installed by the **[CLI installer](../Installer/README.md)** or directly from the Dashboard's **Add Server** dialog (which embeds the same installer). Then install the Dashboard app:

Download **[`PerformanceMonitorDashboard-win-Setup.exe`](https://github.com/erikdarlingdata/PerformanceMonitor/releases/latest)**. Setup.exe installs to `%LocalAppData%\PerformanceMonitorDashboard`, adds Start Menu and Desktop shortcuts, registers under Apps & Features, and wires up auto-update. Launch, add your server, enter credentials, and data appears immediately.

## What Gets Installed

- **PerformanceMonitor database** with collection tables and reporting views
- **Collector stored procedures** for gathering metrics (including SQL Agent job monitoring)
- **Configurable collection** — query text and execution plan capture can be disabled per-collector via `config.collection_schedule` (`collect_query`, `collect_plan` columns) for sensitive or high-volume environments
- **Delta framework** for calculating per-second rates from cumulative DMVs
- **Community dependencies:** sp_WhoIsActive, sp_HealthParser, sp_HumanEventsBlockViewer, sp_BlitzLock
- **SQL Agent jobs:** Collection (every 1 minute), Data Retention (daily at 2:00 AM), and Hung Job Monitor (collection job watchdog, every 5 minutes)
- **Version tracking** in `config.installation_history`

## Data Retention

Default: 30 days (configurable per collector via the `retention_days` column in `config.collection_schedule`).

Storage estimates: 5–10 GB per week, 20–40 GB per month.

## Managed Platform Support

The Full Edition supports Azure SQL Managed Instance and AWS RDS for SQL Server with some limitations (Azure SQL Database is not supported by the Full edition — use Lite there):

| Feature | On-Premises | Azure SQL MI | AWS RDS |
|---|---|---|---|
| All core collectors | Yes | Yes | Yes |
| Default trace collectors | Yes | Disabled automatically | Yes |
| System health XE (file target) | Yes | Disabled automatically | Yes |
| SQL Trace collectors | Yes | Disabled automatically | Yes |
| SQL Agent jobs | Yes | Yes | Yes |
| Running jobs collector | Yes | Yes | Disabled automatically |
| Blocked process threshold | Auto-configured | Auto-configured | Configure via RDS parameter group |
| sp_configure | Yes | Yes | Not available |

**Azure SQL MI:** The installer automatically detects Engine Edition 8 and disables 4 collectors that require file system access or SQL Trace (default_trace, trace_management, trace_analysis, system_health). All other collectors work normally.

**AWS RDS:** The installer automatically detects the `rdsadmin` database and disables the `running_jobs_collector` (requires `msdb.dbo.syssessions` which is restricted on RDS). It also gracefully handles restricted `sp_configure` and limited `msdb` permissions. SQL Agent jobs are created and owned by the installing login. The RDS master user is automatically enrolled in `SQLAgentUserRole`; for other logins, add them to `SQLAgentUserRole` in msdb before running the installer. See the root README's platform notes for RDS Parameter Group configuration of the blocked process threshold.

## Dashboard Tabs

| Tab | Contents |
|---|---|
| **Overview** | Resource overview, daily summary, critical issues, recommendations, server config changes, database config changes, trace flag changes, collection health |
| **Performance** | Performance trends, expensive queries, active queries, query stats, procedure stats, Query Store, Query Store regressions, query trace patterns, query heatmap |
| **Resource Metrics** | Server trends, wait stats, TempDB, file I/O latency, perfmon counters, default trace events, trace analysis, session stats, latch stats, spinlock stats |
| **Memory** | Memory overview, grants, clerks, plan cache, memory pressure events |
| **Locking** | Blocking chains, deadlocks, blocking/deadlock trends, visual block-chain & deadlock-graph viewers |
| **System Events** | Corruption events, contention, errors, I/O issues, scheduler issues, memory conditions |

Plus a NOC-style landing page with server health cards (green/yellow/red severity indicators). Auto-refresh, configurable time ranges, chart drill-down to Active Queries, right-click CSV export, system tray integration, dark and light themes, and timezone display options.

The Dashboard's embedded MCP server exposes 66 read-only tools. See the root README's MCP section for the shared tool surface.

## Troubleshooting

Two diagnostic scripts in the `install/` folder:

| Script | Purpose |
|---|---|
| `99_installer_troubleshooting.sql` | Quick health checks: collection log errors, schedule status, Agent job status, table row counts |
| `99_user_troubleshooting.sql` | Comprehensive diagnostics: runs collectors with `@debug = 1`, detailed timing and row counts |

```sql
SELECT
    collection_time,
    collector_name,
    error_message
FROM PerformanceMonitor.config.collection_log
WHERE collection_status = 'ERROR'
ORDER BY collection_time DESC;
```

**Orphaned `Monitor_LongQueries_*.trc` files (issue #972)** — versions through 2.11.0 accumulated stale SQL Trace files in the SQL Server error log directory. Newer versions bound the long-query trace with a rollover file-count cap, so SQL Server prunes its own files going forward — but trace files already on disk are not removed automatically (`xp_delete_file` cannot delete `.trc` files). Sweep them once with `tools/Remove-OrphanedTraceFiles.ps1`, run **on the SQL Server host** as a local Administrator or the SQL Server service account:

```powershell
.\Remove-OrphanedTraceFiles.ps1 -WhatIf    # preview what would be deleted
.\Remove-OrphanedTraceFiles.ps1            # delete
```

It skips files belonging to a running trace and files that are in use.

## Permissions (On-Premises)

The installer needs `sysadmin` to create the database, Agent jobs, and configure `sp_configure` settings. After installation, the collection jobs can run under a **least-privilege login** with these grants:

```sql
USE [master];
CREATE LOGIN [SQLServerPerfMon] WITH PASSWORD = N'YourStrongPassword';
GRANT VIEW SERVER STATE TO [SQLServerPerfMon];

USE [PerformanceMonitor];
CREATE USER [SQLServerPerfMon] FOR LOGIN [SQLServerPerfMon];
ALTER ROLE [db_owner] ADD MEMBER [SQLServerPerfMon];

/* Direct table grants, deliberately NOT SQLAgentReaderRole: that role gates the sp_help_job*
   procedures, which this product never calls, and grants NO SELECT on the tables the running
   jobs collector actually reads - with only the role, those reads fail with error 229 (#1823).
   These four are exactly what 51_collect_running_jobs.sql and 44_hung_job_monitor.sql read.
   Lite and Darling need a wider set (syscategories, sysjobschedules, and EXECUTE on
   agent_datetime) for collectors this edition does not have - see the main README. */
USE [msdb];
CREATE USER [SQLServerPerfMon] FOR LOGIN [SQLServerPerfMon];
GRANT SELECT ON dbo.sysjobs        TO [SQLServerPerfMon];
GRANT SELECT ON dbo.sysjobactivity TO [SQLServerPerfMon];
GRANT SELECT ON dbo.sysjobhistory  TO [SQLServerPerfMon];
GRANT SELECT ON dbo.syssessions    TO [SQLServerPerfMon];

/* Only if you leave the hung-job monitor's @stop_hung_job at its default of 1: stopping a job
   is an EXECUTE, which no amount of SELECT covers. Without it the auto-stop fails its
   permission check every time it fires - logged to collection_log, not a crash, but the jobs
   it was meant to stop keep running. Withhold it and set @stop_hung_job = 0 so the monitor
   reports rather than acts. */
GRANT EXECUTE ON dbo.sp_stop_job   TO [SQLServerPerfMon];
```

| Grant | Why |
|---|---|
| `VIEW SERVER STATE` | All DMV access (wait stats, query stats, memory, CPU, file I/O, etc.) |
| `db_owner` on PerformanceMonitor | Collectors insert data, create/alter tables, execute procedures. Scoped to just this database — not sysadmin. |
| `SELECT` on the four msdb job tables | Read `sysjobs`, `sysjobactivity`, `sysjobhistory`, `syssessions` for the running jobs collector and the hung-job monitor. These are direct table reads — `SQLAgentReaderRole` alone leaves every one failing with error 229 |
| `EXECUTE` on `msdb.dbo.sp_stop_job` | The hung-job monitor's auto-stop, on by default (`@stop_hung_job = 1`). Withhold it and set `@stop_hung_job = 0`, or the auto-stop fails on permissions every time it fires |

**Optional** (gracefully skipped if missing):
- `ALTER SETTINGS` — installer sets `blocked process threshold` via `sp_configure`. Skipped with a warning if unavailable.
- `ALTER TRACE` — default trace collector. Skipped if denied.
- `DBCC TRACESTATUS` — server config collector skips trace flag detection if denied.

Change the SQL Agent job owner to the new login after installation if you want to run under least privilege end-to-end.

The FinOps Index Analysis tab needs additional per-database grants — see the root README's [FinOps Index Analysis](../README.md#finops-index-analysis-per-database-grants) section (it applies to Full, Lite, and Darling alike).
