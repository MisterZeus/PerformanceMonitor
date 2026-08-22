using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using PerformanceMonitorLite.Services;
using PerformanceMonitor.Common;

namespace PerformanceMonitorLite.Mcp;

[McpServerToolType]
public sealed class McpQueryTools
{
    [McpServerTool(Name = "get_top_queries_by_cpu"), Description("Gets expensive queries from sys.dm_exec_query_stats (plan cache). Best for: currently cached queries with detailed per-execution stats, DOP, spills, and query_hash for trending. Returns query_hash, query_plan_hash, sql_handle, plan_handle, and host_object (the hosting procedure/function for proc-hosted statements, null for ad-hoc) — groups key on (database, query_hash, host_object), so INSERT...EXEC callers in different procedures report separately with their own text. distinct_texts counts statement texts merged into a group (>1 = ad-hoc literal variants or pre-upgrade history; query_text is one representative, 0 means no stored text for the group). Supports database and parallelism filtering. min/max_cpu_ms and min/max_elapsed_ms are LIFETIME extremes for the plan's time in cache (same semantics as max_dop), not windowed — totals and avgs are windowed deltas; rows where an extreme provably predates the window carry extremes_note. Also returns cpu_attribution: the returned rows' summed CPU-seconds against the SQL process's measured CPU-seconds for the window (avg cpu_utilization % x core count x window) - attributed_cpu_ratio says how much of the box the ranking explains; when the CPU series or core count is missing, or covers too little of the window, the ratio is omitted rather than invented.")]
    public static async Task<string> GetTopQueriesByCpu(
        LocalDataService dataService,
        ServerManager serverManager,
        [Description("Server name or display name.")] string? server_name = null,
        [Description("Hours of history. Default 24.")] int hours_back = 24,
        [Description("Number of top queries. Default 20.")] int top = 20,
        [Description("Filter to a specific database.")] string? database_name = null,
        [Description("If true, only return queries whose cached plan has EVER run at DOP > 1. Note: max_dop comes from sys.dm_exec_query_stats and is a lifetime-max for the plan's time in cache, so a plan compiled before MAXDOP was lowered keeps reporting the old higher value until it is evicted or recompiled. Confirm current parallelism with analyze_query_plan, which reads the actual plan.")] bool parallel_only = false,
        [Description("Minimum DOP to filter on. Implies parallel filtering. Filters the same lifetime-max value as parallel_only, not current parallelism.")] int min_dop = 0,
        [Description(McpHelpers.AsOfDescription)] string? as_of = null)
    {
        var (resolved, error) = ServerResolver.ResolveOrError(serverManager, server_name);
        if (error != null) return error;

        try
        {
            var hoursError = McpHelpers.ValidateWindow(hours_back, as_of, out var windowEnd);
            if (hoursError != null) return hoursError;

            var topError = McpHelpers.ValidateTop(top, "top");
            if (topError != null) return topError;

            /* Captured BEFORE the ranking read, whose window is its own internal UtcNow — hoisting
               shrinks the numerator/denominator window skew from the ranking query's full duration to
               call-entry overhead (review catch; threading one instant INTO the shared ranking read's
               signature is the only way to zero it, and sub-microsecond against an hours window does
               not buy that churn). */
            var nowUtc = windowEnd;
            var rows = await dataService.GetTopQueriesByCpuAsync(resolved.ServerId, hours_back, top, databaseNames: string.IsNullOrEmpty(database_name) ? null : new[] { database_name }, asOfUtc: windowEnd);
            if (rows.Count == 0)
            {
                return McpHelpers.Status("unavailable", "No query stats available for the specified time range.");
            }

            var filtered = rows
                .Where(r => !(parallel_only || min_dop > 1) || (r.MaxDop > 1 && r.MaxDop >= (min_dop > 1 ? min_dop : 2)))
                .ToList();

            /* #2320: what fraction of the box's measured CPU the RETURNED rows explain — numerator is
               the caller-visible ranking (post top-N, post filters), denominator is measured, and the
               ratio is omitted rather than invented when a denominator piece is missing. One nowUtc
               backs the aggregate read AND the ratio math, and the two independent reads run
               concurrently (review catches; Darling has both by construction). */
            var cpuAggregateTask = dataService.GetCpuWindowAggregateAsync(resolved.ServerId, nowUtc.AddHours(-hours_back), nowUtc);
            var propertiesTask = dataService.GetLatestServerPropertiesAsync(resolved.ServerId);
            await Task.WhenAll(cpuAggregateTask, propertiesTask);
            var cpuAggregate = await cpuAggregateTask;
            var properties = await propertiesTask;
            var attribution = CpuAttribution.Compute(
                filtered.Sum(r => r.TotalCpuMs) / 1000.0,
                nowUtc.AddHours(-hours_back), nowUtc,
                cpuAggregate.SampleCount, cpuAggregate.FirstSample, cpuAggregate.LastSample, cpuAggregate.AvgSqlCpuPercent,
                properties?.CpuCount ?? 0);

            var result = filtered.Select(r => new
            {
                database_name = r.DatabaseName,
                query_hash = r.QueryHash,
                query_plan_hash = r.QueryPlanHash,
                sql_handle = r.SqlHandle,
                plan_handle = r.PlanHandle,
                execution_count = r.TotalExecutions,
                total_cpu_ms = r.TotalCpuMs,
                total_elapsed_ms = r.TotalElapsedMs,
                avg_cpu_ms = r.AvgCpuMs,
                avg_elapsed_ms = r.AvgElapsedMs,
                min_cpu_ms = r.MinCpuMs,
                max_cpu_ms = r.MaxCpuMs,
                min_elapsed_ms = r.MinElapsedMs,
                max_elapsed_ms = r.MaxElapsedMs,
                /* #2235: min/max are lifetime extremes (see QueryStatExtremes) — flagged only on
                   the provable case, an extreme exceeding the whole window's total. */
                extremes_note = QueryStatExtremes.LifetimeExtremeNote(
                    r.TotalCpuMs, r.MaxCpuMs, r.TotalElapsedMs, r.MaxElapsedMs),
                min_dop = r.MinDop,
                max_dop = r.MaxDop,
                is_parallel = r.MaxDop > 1,
                total_logical_reads = r.TotalLogicalReads,
                total_logical_writes = r.TotalLogicalWrites,
                total_physical_reads = r.TotalPhysicalReads,
                total_rows = r.TotalRows,
                total_spills = r.TotalSpills,
                avg_reads = r.AvgReads,
                // #2012 stage 2: same annotations as Darling's twin — the host object joins the
                // grouping key, so proc-hosted INSERT...EXEC callers sharing a hash land in
                // separate, correctly-labeled rows; null = ad-hoc/prepared text (or pre-upgrade
                // history, which ages out with retention).
                host_object = r.HostObjectName,
                query_text = McpHelpers.Truncate(r.QueryText, 2000),
                distinct_texts = r.DistinctTexts,
                text_note = r.DistinctTexts > 1
                    ? $"this group blends {r.DistinctTexts} distinct statement texts (ad-hoc literal variants; or history predating the host-object split for INSERT...EXEC callers); query_text is one representative"
                    : null
            });

            return JsonSerializer.Serialize(new
            {
                server = resolved.ServerName,
                hours_back,
                cpu_attribution = new
                {
                    ranked_cpu_seconds = attribution.RankedCpuSeconds,
                    sql_cpu_seconds_in_window = attribution.SqlCpuSecondsInWindow,
                    attributed_cpu_ratio = attribution.AttributedCpuRatio,
                    note = attribution.Note
                },
                queries = result
            }, McpHelpers.JsonOptions);
        }
        catch (Exception ex)
        {
            return McpHelpers.FormatError("get_top_queries_by_cpu", ex);
        }
    }

    [McpServerTool(Name = "get_top_procedures_by_cpu"), Description("Gets the most expensive stored procedures ranked by total CPU time. Shows execution counts, CPU/elapsed times, and I/O metrics. Delta-based: requires ~30 minutes after adding a new server before data appears. min/max_cpu_ms and min/max_elapsed_ms are LIFETIME extremes for the plan's time in cache (same semantics as max_dop), not windowed — totals and avgs are windowed deltas; rows where an extreme provably predates the window carry extremes_note. Also returns cpu_attribution: the returned rows' summed CPU-seconds against the SQL process's measured CPU-seconds for the window (avg cpu_utilization % x core count x window) - attributed_cpu_ratio says how much of the box the ranking explains; when the CPU series or core count is missing, or covers too little of the window, the ratio is omitted rather than invented.")]
    public static async Task<string> GetTopProceduresByCpu(
        LocalDataService dataService,
        ServerManager serverManager,
        [Description("Server name or display name.")] string? server_name = null,
        [Description("Hours of history. Default 24.")] int hours_back = 24,
        [Description("Number of top procedures. Default 20.")] int top = 20,
        [Description("Filter to a specific database.")] string? database_name = null,
        [Description(McpHelpers.AsOfDescription)] string? as_of = null)
    {
        var (resolved, error) = ServerResolver.ResolveOrError(serverManager, server_name);
        if (error != null) return error;

        try
        {
            var hoursError = McpHelpers.ValidateWindow(hours_back, as_of, out var windowEnd);
            if (hoursError != null) return hoursError;

            var topError = McpHelpers.ValidateTop(top, "top");
            if (topError != null) return topError;

            /* Same pre-read capture as the queries tool — the skew shrinks to call-entry overhead. */
            var nowUtc = windowEnd;
            var rows = await dataService.GetTopProceduresByCpuAsync(resolved.ServerId, hours_back, top, databaseNames: string.IsNullOrEmpty(database_name) ? null : new[] { database_name }, asOfUtc: windowEnd);
            if (rows.Count == 0)
            {
                return McpHelpers.Status(
                    "unavailable",
                    "No procedure stats available. Delta-based collection requires at least two collection cycles (~30 minutes) to produce non-zero values.");
            }

            /* #2320: same attributed-CPU disclosure as the queries tool — one shared computation, one
               nowUtc backing aggregate and ratio, same concurrent independent reads. */
            var cpuAggregateTask = dataService.GetCpuWindowAggregateAsync(resolved.ServerId, nowUtc.AddHours(-hours_back), nowUtc);
            var propertiesTask = dataService.GetLatestServerPropertiesAsync(resolved.ServerId);
            await Task.WhenAll(cpuAggregateTask, propertiesTask);
            var cpuAggregate = await cpuAggregateTask;
            var properties = await propertiesTask;
            var attribution = CpuAttribution.Compute(
                rows.Sum(r => r.TotalCpuMs) / 1000.0,
                nowUtc.AddHours(-hours_back), nowUtc,
                cpuAggregate.SampleCount, cpuAggregate.FirstSample, cpuAggregate.LastSample, cpuAggregate.AvgSqlCpuPercent,
                properties?.CpuCount ?? 0);

            var result = rows.Select(r => new
            {
                database_name = r.DatabaseName,
                full_name = r.FullName,
                object_type = r.ObjectType,
                sql_handle = r.SqlHandle,
                plan_handle = r.PlanHandle,
                execution_count = r.TotalExecutions,
                total_cpu_ms = r.TotalCpuMs,
                total_elapsed_ms = r.TotalElapsedMs,
                avg_cpu_ms = r.AvgCpuMs,
                avg_elapsed_ms = r.AvgElapsedMs,
                min_cpu_ms = r.MinCpuMs,
                max_cpu_ms = r.MaxCpuMs,
                min_elapsed_ms = r.MinElapsedMs,
                max_elapsed_ms = r.MaxElapsedMs,
                /* #2235: same lifetime-extremes flag as the queries tool. Mirrors Darling. */
                extremes_note = QueryStatExtremes.LifetimeExtremeNote(
                    r.TotalCpuMs, r.MaxCpuMs, r.TotalElapsedMs, r.MaxElapsedMs),
                avg_reads = r.AvgReads,
                total_logical_reads = r.TotalLogicalReads,
                total_logical_writes = r.TotalLogicalWrites,
                total_physical_reads = r.TotalPhysicalReads,
                total_spills = r.TotalSpills
            });

            return JsonSerializer.Serialize(new
            {
                server = resolved.ServerName,
                hours_back,
                cpu_attribution = new
                {
                    ranked_cpu_seconds = attribution.RankedCpuSeconds,
                    sql_cpu_seconds_in_window = attribution.SqlCpuSecondsInWindow,
                    attributed_cpu_ratio = attribution.AttributedCpuRatio,
                    note = attribution.Note
                },
                procedures = result
            }, McpHelpers.JsonOptions);
        }
        catch (Exception ex)
        {
            return McpHelpers.FormatError("get_top_procedures_by_cpu", ex);
        }
    }

    [McpServerTool(Name = "get_query_store_top"), Description("Gets expensive queries from Query Store (persistent, survives restarts). Best for: historical analysis, queries no longer in plan cache. Requires Query Store enabled on target databases. Supports database filtering.")]
    public static async Task<string> GetQueryStoreTop(
        LocalDataService dataService,
        ServerManager serverManager,
        [Description("Server name or display name.")] string? server_name = null,
        [Description("Hours of history. Default 24.")] int hours_back = 24,
        [Description("Number of top queries. Default 20.")] int top = 20,
        [Description("Filter to a specific database.")] string? database_name = null,
        [Description(McpHelpers.AsOfDescription)] string? as_of = null)
    {
        var (resolved, error) = ServerResolver.ResolveOrError(serverManager, server_name);
        if (error != null) return error;

        try
        {
            var hoursError = McpHelpers.ValidateWindow(hours_back, as_of, out var windowEnd);
            if (hoursError != null) return hoursError;

            var topError = McpHelpers.ValidateTop(top, "top");
            if (topError != null) return topError;

            var rows = await dataService.GetQueryStoreTopQueriesAsync(resolved.ServerId, hours_back, top, databaseNames: string.IsNullOrEmpty(database_name) ? null : new[] { database_name }, asOfUtc: windowEnd);
            if (rows.Count == 0)
            {
                return McpHelpers.Status("unavailable", "No Query Store data available. Query Store may not be enabled on target databases.");
            }

            var result = rows.Select(r => new
            {
                database_name = r.DatabaseName,
                query_id = r.QueryId,
                plan_id = r.PlanId,
                query_hash = r.QueryHash,
                query_plan_hash = r.QueryPlanHash,
                execution_count = r.TotalExecutions,
                avg_duration_ms = r.AvgDurationMs,
                avg_cpu_ms = r.AvgCpuTimeMs,
                avg_logical_reads = r.AvgLogicalReads,
                avg_logical_writes = r.AvgLogicalWrites,
                avg_physical_reads = r.AvgPhysicalReads,
                avg_rowcount = r.AvgRowcount,
                last_execution_time = r.LastExecutionTime?.ToString("o"),
                query_text = McpHelpers.Truncate(r.QueryText, 2000)
            });

            return JsonSerializer.Serialize(new
            {
                server = resolved.ServerName,
                hours_back,
                queries = result
            }, McpHelpers.JsonOptions);
        }
        catch (Exception ex)
        {
            return McpHelpers.FormatError("get_query_store_top", ex);
        }
    }

    [McpServerTool(Name = "get_query_store_regressions"), Description("Finds queries whose Query Store performance got WORSE, by comparing each (database, query_id) group's averages inside a recent window against its baseline - every capture BEFORE that window. Returns baseline vs recent duration, CPU and logical reads with the regression percent for each, the execution-count-weighted extra duration (the ranking key: a 5 ms regression executed a million times outranks a 5-second one executed twice), the plan counts on both sides, and a duration-driven severity band. get_query_store_top answers what is EXPENSIVE; the most expensive query is usually the one that always was. This answers what CHANGED. Rows are kept only where average CPU regressed by more than 25%.")]
    public static async Task<string> GetQueryStoreRegressions(
        LocalDataService dataService,
        ServerManager serverManager,
        [Description("Server name or display name.")] string? server_name = null,
        [Description("Size of the RECENT window, in hours back from now. Everything collected before it is the baseline. Default 24.")] int hours_back = 24,
        [Description("Limit to one database. Omit for all databases.")] string? database_name = null,
        [Description("Maximum rows to return, worst first. Default 50 (the number the desktop viewer shows).")] int limit = 50,
        [Description(McpHelpers.AsOfDescription)] string? as_of = null)
    {
        var (resolved, error) = ServerResolver.ResolveOrError(serverManager, server_name);
        if (error != null) return error;

        try
        {
            var validation = McpHelpers.ValidateWindow(hours_back, as_of, out var windowEnd) ?? McpHelpers.ValidateTop(limit);
            if (validation != null) return validation;

            /*
                Over-fetch by one. Comparing the row count to the cap reports truncation for a server that
                happens to have exactly `limit` regressions and nothing more, which is a false positive in
                the one field whose whole reason for existing is that the cap should not have to be inferred.
            */
            var databases = string.IsNullOrWhiteSpace(database_name) ? null : new[] { database_name };
            var rows = await dataService.GetQueryStoreRegressionsAsync(
                resolved.ServerId, hours_back, limit + 1, databases, asOfUtc: windowEnd);

            if (rows.Count == 0)
                return await EmptyRegressionsAsync(dataService, resolved.ServerId, resolved.ServerName, hours_back, windowEnd);

            var truncated = rows.Count > limit;

            return JsonSerializer.Serialize(new
            {
                server = resolved.ServerName,
                hours_back,
                database_name,
                /*
                    Named so the caller cannot mistake which side is which. "baseline" is NOT a fixed
                    lookback: it is everything collected before the window, so a longer hours_back makes
                    the recent window bigger AND the baseline shorter.
                */
                baseline_is = "every Query Store capture collected BEFORE the recent window",
                gate = "average CPU regressed by more than 25%",
                regression_count = Math.Min(rows.Count, limit),
                truncated,
                regressions = rows.Take(limit).Select(r => new
                {
                    database_name = r.DatabaseName,
                    query_id = r.QueryId,
                    severity = r.Severity,
                    baseline_duration_ms = r.BaselineDurationMs,
                    recent_duration_ms = r.RecentDurationMs,
                    duration_regression_percent = r.DurationRegressionPercent,
                    baseline_cpu_ms = r.BaselineCpuMs,
                    recent_cpu_ms = r.RecentCpuMs,
                    cpu_regression_percent = r.CpuRegressionPercent,
                    baseline_reads = r.BaselineReads,
                    recent_reads = r.RecentReads,
                    io_regression_percent = r.IoRegressionPercent,
                    /* The ranking key, and the one number that says whether this regression MATTERS. */
                    additional_duration_ms = r.AdditionalDurationMs,
                    baseline_exec_count = r.BaselineExecCount,
                    recent_exec_count = r.RecentExecCount,
                    /* A plan count that moved between the two sides is the first thing to check: a query
                       that regressed while gaining a plan is usually a plan-choice problem, not a data one. */
                    baseline_plan_count = r.BaselinePlanCount,
                    recent_plan_count = r.RecentPlanCount,
                    last_execution_time = r.LastExecutionTime?.ToString("o"),
                    query_text = r.QueryTextSample,
                }),
            }, McpHelpers.JsonOptions);
        }
        catch (Exception ex)
        {
            return McpHelpers.FormatError("get_query_store_regressions", ex);
        }
    }

    /// <summary>
    /// What zero regressions actually means, which is four different things — word for word with Darling.
    /// <para>Only ONE of them is good news, and the other three look identical to it in a bare empty array.
    /// The dangerous one is a server whose entire collected history sits INSIDE the requested window: it has
    /// no BEFORE, so it can never show a regression however badly it regressed, and answering "no
    /// regressions" there is a confident wrong answer rather than a missing one.</para>
    /// </summary>
    private static async Task<string> EmptyRegressionsAsync(
        LocalDataService dataService, int serverId, string serverName, int hours_back, DateTime windowEnd)
    {
        /* The anchor is threaded in rather than resolved again: the coverage probe answers "does a BEFORE
           exist for this window", and a window it computed for itself would be a different one. */
        var (hasBaseline, hasRecent) = await dataService.GetQueryStoreRegressionCoverageAsync(serverId, hours_back, asOfUtc: windowEnd);

        if (!hasBaseline && !hasRecent)
        {
            return McpHelpers.Status(
                "unavailable",
                $"No Query Store data has EVER been collected for {serverName}, so this is NOT a report of zero regressions — there is nothing to compare. Query Store may be OFF on this server's databases, which get_query_store_health will say; otherwise check that collection is running for this server.");
        }

        if (!hasBaseline)
        {
            return McpHelpers.Status(
                "unavailable",
                $"Every Query Store capture for {serverName} falls INSIDE the last {hours_back} hour(s), so there is no baseline to compare against and no regression can be detected however badly one regressed. This is NOT a clean bill of health. Shorten hours_back so more of the collected history falls before the window, or wait until this server has history older than it.");
        }

        if (!hasRecent)
        {
            return McpHelpers.Status(
                "empty",
                $"{serverName} has Query Store history from before this window but nothing collected IN the last {hours_back} hour(s), so there is a recent side missing rather than nothing to report. Widen hours_back, or check get_collection_health — a collector that stopped looks exactly like this.");
        }

        return McpHelpers.Status(
            "empty",
            $"No query on {serverName} regressed in the last {hours_back} hour(s). Both a baseline and this window were collected and no query's average CPU is more than 25% worse than its baseline — this IS the all-clear for this read.");
    }

    [McpServerTool(Name = "get_query_duration_trend"), Description("Gets a time-series of average query duration over time. Useful for spotting overall performance degradation or improvement trends across all queries.")]
    public static async Task<string> GetQueryDurationTrend(
        LocalDataService dataService,
        ServerManager serverManager,
        [Description("Server name or display name.")] string? server_name = null,
        [Description("Hours of history. Default 24.")] int hours_back = 24,
        [Description(McpHelpers.AsOfDescription)] string? as_of = null)
    {
        var (resolved, error) = ServerResolver.ResolveOrError(serverManager, server_name);
        if (error != null) return error;

        try
        {
            var hoursError = McpHelpers.ValidateWindow(hours_back, as_of, out var windowEnd);
            if (hoursError != null) return hoursError;

            var points = await dataService.GetQueryDurationTrendAsync(resolved.ServerId, hours_back);

            if (points.Count == 0)
            {
                /* Same two states again, same words as Darling's twin. The probe reads v_query_stats
                   because THIS trend does; Darling's probes the base table because ITS trend does. Each
                   probe follows its own read — what the caller sees is one sentence, not two. */
                return await dataService.HasAnyQueryStatAsync(resolved.ServerId)
                    ? McpHelpers.Status(
                        "empty",
                        $"No query samples recorded for {resolved.ServerName} in the last {hours_back} hour(s). This server HAS collected query stats before, so this window is genuinely quiet rather than broken — widen hours_back to find the most recent samples.")
                    : McpHelpers.Status(
                        "unavailable",
                        $"No query stats have EVER been recorded for {resolved.ServerName}. This is not an empty window — the query_stats collector has stored nothing at all for this server. Check that collection is running and that the server is enabled; get_top_queries_by_cpu will be equally empty until it does.");
            }

            /* The two siblings below serialize through the SAME helper, so the three Performance-Trends
               reads cannot advertise three different field sets for one shape. */
            return SerializeTrend(resolved.ServerName, hours_back, points);
        }
        catch (Exception ex)
        {
            return McpHelpers.FormatError("get_query_duration_trend", ex);
        }
    }

    [McpServerTool(Name = "get_procedure_duration_trend"), Description("Gets a time-series of stored-procedure elapsed time per second and executions per second over time, summed across every procedure. The sibling of get_query_duration_trend, and NOT a duplicate of it: query_stats attributes a procedure's work to the individual statements inside it, so a procedure that got slower is smeared across however many statements it runs. This charges the whole call to the procedure. Read the two together to tell an ad-hoc SQL regression from a procedure regression.")]
    public static async Task<string> GetProcedureDurationTrend(
        LocalDataService dataService,
        ServerManager serverManager,
        [Description("Server name or display name.")] string? server_name = null,
        [Description("Hours of history. Default 24.")] int hours_back = 24,
        [Description(McpHelpers.AsOfDescription)] string? as_of = null)
    {
        var (resolved, error) = ServerResolver.ResolveOrError(serverManager, server_name);
        if (error != null) return error;

        try
        {
            var hoursError = McpHelpers.ValidateWindow(hours_back, as_of, out var windowEnd);
            if (hoursError != null) return hoursError;

            var points = await dataService.GetProcedureDurationTrendAsync(resolved.ServerId, hours_back, asOfUtc: windowEnd);
            if (points.Count == 0)
            {
                return await EmptyTrendAsync(
                    dataService.HasAnyProcedureStatAsync(resolved.ServerId), resolved.ServerName, hours_back,
                    "stored-procedure",
                    "Check that collection is running and that the server is enabled. A server that genuinely runs no stored procedures also lands here, and that is a real answer rather than a fault.");
            }

            return SerializeTrend(resolved.ServerName, hours_back, points);
        }
        catch (Exception ex)
        {
            return McpHelpers.FormatError("get_procedure_duration_trend", ex);
        }
    }

    [McpServerTool(Name = "get_query_store_duration_trend"), Description("Gets a time-series of Query Store duration per second and executions per second over time, summed across every query. Where get_query_duration_trend reads the plan cache and loses everything an eviction or a restart takes with it, this reads Query Store, which persists per interval - so it is the series that survives a failover and the one to reach for when a regression is older than the cache. Each interval is counted once, at the hour the work ran.")]
    public static async Task<string> GetQueryStoreDurationTrend(
        LocalDataService dataService,
        ServerManager serverManager,
        [Description("Server name or display name.")] string? server_name = null,
        [Description("Hours of history. Default 24.")] int hours_back = 24,
        [Description(McpHelpers.AsOfDescription)] string? as_of = null)
    {
        var (resolved, error) = ServerResolver.ResolveOrError(serverManager, server_name);
        if (error != null) return error;

        try
        {
            var hoursError = McpHelpers.ValidateWindow(hours_back, as_of, out var windowEnd);
            if (hoursError != null) return hoursError;

            var points = await dataService.GetQueryStoreDurationTrendAsync(resolved.ServerId, hours_back, asOfUtc: windowEnd);
            if (points.Count == 0)
            {
                /*
                    The one empty answer here that is NOT about the collector: Query Store can be off on
                    every database on the instance. A server with no Query Store data is not a server with
                    no slow queries, so the message names that cause first.
                */
                return await EmptyTrendAsync(
                    dataService.HasAnyQueryStoreStatAsync(resolved.ServerId), resolved.ServerName, hours_back,
                    "Query Store",
                    "Query Store may be OFF on this server's databases — that, not an absence of slow queries, is the usual cause. Check QUERY_STORE = ON per database, then that collection is running for this server.");
            }

            return SerializeTrend(resolved.ServerName, hours_back, points);
        }
        catch (Exception ex)
        {
            return McpHelpers.FormatError("get_query_store_duration_trend", ex);
        }
    }

    /// <summary>
    /// The one payload shape the three Performance-Trends siblings share, so a caller can chart them on one
    /// axis without learning three field names. Darling serializes the same fields.
    /// <para><c>execution_count</c> and <c>executions_per_second</c> are the SAME quantity. The first
    /// shipped truncated to an integer, which on a quiet server turns 0.4 executions a second into a
    /// reported ZERO - an idle server, when the truth was a slow one. It is kept so a consumer reading it
    /// does not break; read <c>executions_per_second</c>.</para>
    /// </summary>
    private static string SerializeTrend(string serverName, int hours_back, List<QueryTrendPoint> points) =>
        JsonSerializer.Serialize(new
        {
            server = serverName,
            hours_back,
            trend = points.Select(p => new
            {
                time = p.CollectionTime.ToString("o"),
                value = p.Value,
                execution_count = p.ExecutionCount,
                executions_per_second = p.ExecutionsPerSecond,
            }),
        }, McpHelpers.JsonOptions);

    /// <summary>
    /// The two-branch empty answer the two new Performance-Trends siblings share (#2484), word for word with
    /// Darling's <c>DarlingMcpTrendTools.EmptyTrendAsync</c> and phrased like the get_query_duration_trend
    /// answer that already ships -- a user moving between the SKUs, or between the three trends, must not be
    /// told a different story about the same state.
    /// </summary>
    private static async Task<string> EmptyTrendAsync(
        Task<bool> probe, string serverName, int hours_back, string what, string checkThis)
    {
        var everSampled = await probe;
        return everSampled
            ? McpHelpers.Status(
                "empty",
                $"No {what} samples were recorded for {serverName} in the last {hours_back} hour(s). This server HAS been sampled before, so this window is genuinely quiet rather than broken — widen hours_back to find the most recent samples.")
            : McpHelpers.Status(
                "unavailable",
                $"No {what} samples have EVER been recorded for {serverName}. This is not an empty window — nothing at all has been stored for this server, so it is NOT a quiet server. {checkThis}");
    }

    [McpServerTool(Name = "get_query_trend"), Description("Gets a time-series of performance metrics for a specific query identified by its query_hash. Use this after identifying a problematic query from get_top_queries_by_cpu or get_query_store_top to see how it has changed over time.")]
    public static async Task<string> GetQueryTrend(
        LocalDataService dataService,
        ServerManager serverManager,
        [Description("The query_hash value from get_top_queries_by_cpu or get_query_store_top.")] string query_hash,
        [Description("The database name the query belongs to.")] string database_name,
        [Description("Server name or display name.")] string? server_name = null,
        [Description("Hours of history. Default 24.")] int hours_back = 24,
        [Description(McpHelpers.AsOfDescription)] string? as_of = null)
    {
        var (resolved, error) = ServerResolver.ResolveOrError(serverManager, server_name);
        if (error != null) return error;

        try
        {
            var hoursError = McpHelpers.ValidateWindow(hours_back, as_of, out var windowEnd);
            if (hoursError != null) return hoursError;

            var rows = await dataService.GetQueryStatsHistoryAsync(resolved.ServerId, database_name, query_hash, hours_back, asOfUtc: windowEnd);
            if (rows.Count == 0)
            {
                return McpHelpers.Status("empty", $"No history found for query_hash '{query_hash}' in database '{database_name}' within the last {hours_back} hours.");
            }

            var result = rows.Select(r => new
            {
                collection_time = r.CollectionTime.ToString("o"),
                execution_count = r.DeltaExecutions,
                cpu_ms = Math.Round(r.DeltaCpuMs, 2),
                elapsed_ms = Math.Round(r.DeltaElapsedMs, 2),
                avg_cpu_ms = Math.Round(r.AvgCpuMs, 2),
                avg_elapsed_ms = Math.Round(r.AvgElapsedMs, 2),
                logical_reads = r.DeltaLogicalReads,
                logical_writes = r.DeltaLogicalWrites,
                physical_reads = r.DeltaPhysicalReads,
                rows = r.DeltaRows,
                spills = r.DeltaSpills,
                min_dop = r.MinDop,
                max_dop = r.MaxDop,
                query_plan_hash = r.QueryPlanHash
            });

            return JsonSerializer.Serialize(new
            {
                server = resolved.ServerName,
                database_name,
                query_hash,
                hours_back,
                data_points = rows.Count,
                trend = result
            }, McpHelpers.JsonOptions);
        }
        catch (Exception ex)
        {
            return McpHelpers.FormatError("get_query_trend", ex);
        }
    }
}
