# Performance Monitor Lite

Lightweight, agentless SQL Server performance monitoring desktop application. Monitors multiple SQL Server instances from a single dashboard without installing anything on target servers. Queries DMVs directly over the network and stores data locally in DuckDB with automatic Parquet archival.

Includes an embedded MCP server for exposing monitoring data to LLM clients (Claude Code, Cursor, etc.) via the Model Context Protocol.

Best for quick triage, Azure SQL Database, restricted environments, and consultant use.

## Prerequisites

**Which .NET runtimes you need depends on which artifact you take, and the two answers are different.** Both ship from the same release.

| Artifact | Publish shape | .NET runtimes to install first |
|---|---|---|
| `PerformanceMonitorLite-win-Setup.exe` (recommended) | **self-contained** (`--self-contained -r win-x64`) | **None.** It carries its own runtime |
| `PerformanceMonitorLite-<version>.zip` (portable) | framework-dependent | **Both** of the two below |

For the ZIP, install both, x64, from <https://dotnet.microsoft.com/download/dotnet/10.0>:

- **.NET Desktop Runtime 10** — the WPF application itself.
- **ASP.NET Core Runtime 10** — required **unconditionally**, which is the part nobody expects. `PerformanceMonitorLite.csproj` references `ModelContextProtocol.AspNetCore`, and that package brings the `Microsoft.AspNetCore.App` framework reference in transitively, so the built `PerformanceMonitorLite.runtimeconfig.json` names **three** frameworks — `Microsoft.NETCore.App`, `Microsoft.WindowsDesktop.App` and `Microsoft.AspNetCore.App` — whether or not the MCP server is ever switched on. Turning MCP off in settings does not remove the requirement; it is decided at build time, not at run time.

A stock Windows Server image has neither runtime.

**If one is missing, nothing of ours is on screen.** The .NET host resolves the frameworks named in the runtimeconfig before a single line of Lite’s code runs, so the failure is the host’s own `You must install .NET to run this application`, with no product branding and no instructions. It also reports only the **first** framework it cannot find: install just the Desktop Runtime and the next launch fails again, identically, naming `Microsoft.AspNetCore.App`. Install both up front and skip the round trip.

Lite is launched by double-clicking an exe and has no install script, so there is nowhere to put a pre-flight gate the way [Darling’s `install-darling.ps1`](../Darling/tools/install-darling.ps1) does (it refuses an install without the ASP.NET Core runtime and warns without the Desktop runtime). What ships instead is `READ-ME-FIRST.txt`, in the ZIP beside `PerformanceMonitorLite.exe` — the one place of ours a reader can reach after the host error, because it is the folder they just unzipped.

Monitored SQL Servers need nothing installed on them either way.

See the [root README](../README.md) for full documentation.
