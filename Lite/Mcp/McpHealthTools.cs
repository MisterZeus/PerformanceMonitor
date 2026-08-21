using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using PerformanceMonitorLite.Services;
using PerformanceMonitor.Common;

namespace PerformanceMonitorLite.Mcp;

[McpServerToolType]
public sealed class McpHealthTools
{
    [McpServerTool(Name = "get_server_summary"), Description("Gets a quick health overview for a SQL Server instance: current CPU %, memory usage, recent blocking count, and deadlock count. Use this for a fast health check before drilling into specific areas.")]
    public static async Task<string> GetServerSummary(
        LocalDataService dataService,
        ServerManager serverManager,
        [Description("Server name or display name. Optional if only one server is configured.")] string? server_name = null)
    {
        var (resolved, error) = ServerResolver.ResolveOrError(serverManager, server_name);
        if (error != null) return error;

        try
        {
            var summary = await dataService.GetServerSummaryAsync(resolved.ServerId, resolved.ServerName);
            if (summary == null)
            {
                return McpHelpers.Status(
                    "unavailable",
                    $"No data available for {resolved.ServerName}. The collector may not have run yet.");
            }

            return JsonSerializer.Serialize(new
            {
                server = resolved.ServerName,
                cpu_percent = summary.CpuPercent,
                memory_mb = summary.MemoryMb,
                blocking_count = summary.BlockingCount,
                deadlock_count = summary.DeadlockCount,
                last_collection = summary.LastCollectionTime?.ToString("o")
            }, McpHelpers.JsonOptions);
        }
        catch (Exception ex)
        {
            return McpHelpers.FormatError("get_server_summary", ex);
        }
    }

    [McpServerTool(Name = "get_daily_summary"), Description("Gets a daily health summary: overall composite health band (Healthy/Warning/Critical), total wait time, top wait type, unique query count, deadlocks, blocking events, memory pressure (and severe memory pressure), high-CPU samples, collection errors, and actionable alert count for one day. Use this for a quick overview to decide which areas need investigation.")]
    public static async Task<string> GetDailySummary(
        LocalDataService dataService,
        ServerManager serverManager,
        [Description("Server name or display name.")] string? server_name = null,
        [Description("Summary date (yyyy-MM-dd), interpreted as a UTC day. Default is today.")] string? summary_date = null)
    {
        var (resolved, error) = ServerResolver.ResolveOrError(serverManager, server_name);
        if (error != null) return error;

        DateTime? date = null;
        if (!string.IsNullOrEmpty(summary_date))
        {
            if (!DateTime.TryParse(summary_date, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed))
                return $"Invalid date format '{summary_date}'. Use yyyy-MM-dd format (e.g., 2026-07-09).";
            date = parsed;
        }

        try
        {
            var row = await dataService.GetDailySummaryAsync(resolved.ServerId, date);
            if (row == null || !row.HasData)
            {
                var missDate = row?.SummaryDate ?? date ?? DateTime.UtcNow.Date;
                return McpHelpers.Status(
                    "empty",
                    $"No data collected for {resolved.ServerName} on {missDate:yyyy-MM-dd}.",
                    new { summary_date = missDate.ToString("yyyy-MM-dd"), overall_health = row?.OverallHealth });
            }

            return JsonSerializer.Serialize(new
            {
                server = resolved.ServerName,
                summary_date = row.SummaryDate.ToString("yyyy-MM-dd"),
                overall_health = row.OverallHealth,
                health_band = row.HealthBand.ToString(),
                total_wait_time_sec = row.TotalWaitTimeSec,
                top_wait_type = row.TopWaitType,
                unique_queries = row.UniqueQueries,
                deadlock_count = row.DeadlockCount,
                blocking_events = row.BlockingEvents,
                high_cpu_events = row.HighCpuEvents,
                memory_pressure_events = row.MemoryPressureEvents,
                memory_critical_events = row.MemoryCriticalEvents,
                collection_errors = row.CollectionErrors,
                alert_count = row.AlertCount,
                max_block_duration_ms = row.MaxBlockDurationMs
            }, McpHelpers.JsonOptions);
        }
        catch (Exception ex)
        {
            return McpHelpers.FormatError("get_daily_summary", ex);
        }
    }

    [McpServerTool(Name = "get_collection_health"), Description("Shows the health status of all data collectors for a server — whether they're running successfully, failing, or stale. Check this before investigating data to ensure collectors are working properly. Each row also carries last_note/note_count: what a NON-failing run reported, e.g. an enumeration that came back with 0 items. note_count equal to total_runs means the collector has been collecting nothing all window — not a fault (the target may be legitimately empty), but the reason a HEALTHY collector can still have no data. target_has_user_databases tells those two apart: true means the target DID have user databases in the same window, so an all-window empty enumeration is worth investigating (a login that cannot enter them, an exclusion filter that matched everything); false means either no user databases or no inventory to go on. The sweep_pressure block is the server-level roll-up: it compares the collectors' combined execution demand (average duration amortized by cadence) against the minute the fastest cadence holds. SATURATED means the collection body cannot fit inside its cadence, so relaunches are skipped and the server collects at a multiple of its configured interval while every collector still reads healthy — heaviest_collectors names where that budget goes. That verdict is the SUSTAINED answer only. peak_cycle_risk is the separate single-sweep answer: peak_cycle_ms is what the body costs on the cycle where every scheduled cadence comes due together, and BODY_OVERRUN means that one body cannot fit the budget even when the verdict reads OK — the signature of one infrequent heavy collector, which amortization hides and heaviest_collectors therefore ranks out of sight. peak_collector names it, and peak_cycle_note explains it. Read both fields: a server can be OK/BODY_OVERRUN (a schedule-shape problem, fix by moving or splitting that collector) or SATURATED/BODY_OVERRUN (a capacity problem). Every collector row carries avg_duration_ms, p95_duration_ms and max_duration_ms, because a collector's runs are not always one population: query_store on one dogfood server averaged 13,834 ms over 1,155 runs of which 958 yielded nothing and cost about 36 ms, which puts the other 197 at roughly 80,900 ms EACH - each one, on its own, larger than the whole sweep budget. Read the three together: avg close to p95 close to max is one population, avg far below p95 is two, and p95 far below max is one pathological run. peak_cycle_ms is built from p95 (floored at the mean, so it can never read lower than a mean-based figure) for exactly that reason, and peak_collector carries peak_run_ms beside avg_duration_ms so the gap is visible. Those three still describe RUNS, and five collectors (query_store, plan_correction, query_store_health, index_object_stats, database_scoped_config) run once per DATABASE and write one blended row, so no run-level statistic can say which database cost what. The per-collector `fanout` block is that answer, null for a collector that does not fan out: `items` is how wide the fan-out was, `slowest`/`slowest_ms` name the dearest database and its cost on the window's worst run, `run_ms` is that whole run, and `dominance` is slowest_ms * items / run_ms — 1.0 for a perfectly even fan-out, rising with concentration. It matters because the remedies diverge there: near 1.0 the cost is the fan-out's WIDTH and bounded parallelism is the lever, while around 2.0 or above one database dominates and a per-database schedule override or a stagger is what helps. Do not try to infer this from p95 versus avg — on a per-database collector that ratio is usually saturated by empty-versus-productive runs and says nothing about databases.")]
    public static async Task<string> GetCollectionHealth(
        LocalDataService dataService,
        ServerManager serverManager,
        [Description("Server name or display name.")] string? server_name = null)
    {
        var (resolved, error) = ServerResolver.ResolveOrError(serverManager, server_name);
        if (error != null) return error;

        try
        {
            var rows = await dataService.GetCollectionHealthAsync(resolved.ServerId);
            if (rows.Count == 0)
            {
                return McpHelpers.Status("unavailable", "No collection health data available.");
            }

            var result = rows.Select(r => new
            {
                collector = r.CollectorName,
                status = r.HealthStatus,
                total_runs = r.TotalRuns,
                errors = r.ErrorCount,
                /* Deliberate 1s lock-timeout yields (#1805) — benign, distinct from errors; clustering
                   here is a lock-contention signal about the monitored server. */
                yields = r.YieldCount,
                failure_rate_pct = Math.Round(r.FailureRatePercent, 1),
                avg_duration_ms = Math.Round(r.AvgDurationMs, 0),
                /* #2460: the mean above is a blend whenever a collector's runs come in two sizes, and
                   on this fleet one of them plainly does — query_store averaged 13,834 ms over 1,155
                   runs where 958 yielded nothing at ~36 ms, which puts the other 197 at ~80,900 ms
                   each. p95 is what a HEAVY run of this collector costs and is what the peak-cycle
                   arithmetic below is built from; max is carried beside it so a routine tail can be
                   told from a single pathological cycle, which is the one thing a max alone cannot
                   say about itself. Read the three together: avg ~= p95 ~= max is one population,
                   avg << p95 is two, and p95 << max is one bad run. */
                p95_duration_ms = Math.Round(r.P95DurationMs, 0),
                max_duration_ms = Math.Round(r.MaxDurationMs, 0),
                last_success = r.LastSuccessTime?.ToString("o"),
                last_error = r.LastError,
                /* #1837: what a NON-failing run reported — an enumeration that came back with 0 items,
                   items whose enumeration probe failed. note_count == total_runs means every run in the
                   window came back that way, which is the "collecting nothing for weeks" case that reads
                   as HEALTHY (correctly — an empty target is not a fault) and needs saying out loud. */
                last_note = r.LastNote,
                note_count = r.NoteCount,
                /* #1852: whether the store saw user databases on this target in the same window. The
                   fact that separates "nothing to collect" from "collecting nothing" — a caller
                   diagnosing an empty collector gets it as a boolean instead of parsing it out of the
                   sentence below. False also means "no inventory to go on", never "no databases". */
                target_has_user_databases = r.TargetHasUserDatabases,
                /* The same string both WPF grids render, composed on this side so the web dashboard and
                   any other consumer cannot re-derive it differently. */
                note_summary = CollectorHealthClassifier.FormatCollectionNote(
                    r.LastNote, r.NoteCount, r.TotalRuns, r.CollectorName, r.TargetHasUserDatabases),
                /* #2472: the per-database breakdown of a collector that fans out, null for one that does
                   not. A nested object rather than four sibling fields so a consumer cannot read a
                   slowest item without the width it has to be judged against — the parts only mean
                   something together, and `dominance` is that meaning. Field-for-field Darling's. */
                fanout = r.FanoutDominance is null ? null : new
                {
                    items = r.FanoutItems,
                    slowest = r.SlowestItem,
                    slowest_ms = r.SlowestItemMs,
                    run_ms = r.SlowestRunDurationMs,
                    dominance = Math.Round(r.FanoutDominance.Value, 2)
                }
            });

            /* #2296: the roll-up that makes half-rate collection visible. Every collector on a saturated
               server reads HEALTHY — from each one's own seat nothing is wrong — so the condition only
               existed as a service-log warning ("collection body has not completed … skipping relaunch").
               The verdict compares the collectors' combined execution demand (average duration amortized
               by cadence) against the minute the fastest cadence holds; heaviest_collectors names where
               the budget goes, which is the actionable half of the answer. */
            var pressure = SweepPressureClassifier.Compute(
                rows.Select(r => (r.CollectorName, r.AvgDurationMs, r.P95DurationMs, r.FrequencyMinutes)));
            var heaviest = rows
                .Where(r => r.FrequencyMinutes > 0 && r.AvgDurationMs > 0)
                .OrderByDescending(r => r.AvgDurationMs / r.FrequencyMinutes)
                .Take(3)
                .Select(r => new
                {
                    collector = r.CollectorName,
                    avg_duration_ms = Math.Round(r.AvgDurationMs, 0),
                    p95_duration_ms = Math.Round(r.P95DurationMs, 0),
                    max_duration_ms = Math.Round(r.MaxDurationMs, 0),
                    frequency_minutes = r.FrequencyMinutes,
                    /* #2446: the ranking key said out loud, beside the single-run cost it is derived from.
                       The list still ranks by amortized contribution, because that is what explains
                       busy_percent — but an operator reading it to find the collector that overran a body
                       was reading the wrong column with nothing on the row to say so. */
                    amortized_ms_per_minute = Math.Round(r.AvgDurationMs / r.FrequencyMinutes, 0),
                    /* #2460: "% of the budget PER RUN" now comes from the run that actually costs
                       something — PeakRunMs, the p95 floored at the mean — rather than from a mean that
                       on a bimodal collector describes no run at all. It is the same number the peak
                       cycle charges this collector, so the column and the cycle reconcile by hand;
                       taken from the mean, this row said query_store cost 23% of a body when its heavy
                       run costs 135% of one. Through the shared helper rather than re-derived here, so
                       the floor rule cannot drift between the two SKUs' tools. */
                    pct_of_sweep_budget_per_run = Math.Round(
                        SweepPressureClassifier.PeakRunMs(r.AvgDurationMs, r.P95DurationMs) / SweepPressureClassifier.SweepBudgetMs * 100.0, 1)
                });

            /* #2446: the collector that owns the most of ONE sweep, which is a different collector from
               the ones above whenever it is infrequent enough for amortization to hide it. Named on every
               server, not only on BODY_OVERRUN — knowing where a body's time concentrates is worth having
               before it is a problem, and this is exactly the row heaviest_collectors ranks out of sight. */
            var peakCollector = pressure.PeakCollectorName == null ? null : new
            {
                collector = pressure.PeakCollectorName,
                /* #2460: what one aligned body is charged for this collector — its p95, floored at its
                   mean — with the mean kept beside it, because on a bimodal collector the GAP between
                   the two is the finding. amortized_ms_per_minute stays derived from the mean: that is
                   what amortization means, and a rate built from a tail would claim work the server
                   never sustains. */
                peak_run_ms = Math.Round(pressure.PeakCollectorPeakRunMs, 0),
                avg_duration_ms = Math.Round(pressure.PeakCollectorAvgDurationMs, 0),
                frequency_minutes = pressure.PeakCollectorFrequencyMinutes,
                amortized_ms_per_minute = Math.Round(pressure.PeakCollectorAvgDurationMs / pressure.PeakCollectorFrequencyMinutes, 0),
                pct_of_sweep_budget_per_run = Math.Round(pressure.PeakCollectorPeakRunMs / SweepPressureClassifier.SweepBudgetMs * 100.0, 1)
            };
            var peakCycleNote = SweepPressureClassifier.FormatPeakCycleNote(pressure);

            return JsonSerializer.Serialize(new
            {
                server = resolved.ServerName,
                sweep_pressure = new
                {
                    busy_ms_per_minute = Math.Round(pressure.BusyMsPerMinute, 0),
                    busy_percent = Math.Round(pressure.BusyPercent, 1),
                    verdict = pressure.Verdict,
                    /* #2446: the second dimension, and deliberately NOT folded into verdict. verdict
                       answers "does sustained demand fit the cadence on average"; this answers "does one
                       scheduled body fit at all". They disagree exactly when an infrequent heavy collector
                       owns most of a single sweep — which an amortized number cannot see by construction,
                       since dividing by that collector's own long cadence is what makes it small. Its own
                       vocabulary (FITS / BODY_OVERRUN) so it can never be read as a fourth verdict band,
                       and its own field so a fleet scan can filter on it. */
                    peak_cycle_ms = Math.Round(pressure.PeakCycleMs, 0),
                    peak_cycle_percent = Math.Round(pressure.PeakCyclePercent, 1),
                    peak_cycle_risk = pressure.PeakCycleRisk,
                    peak_collector = peakCollector,
                    peak_cycle_note = string.IsNullOrEmpty(peakCycleNote) ? null : peakCycleNote,
                    heaviest_collectors = heaviest,
                    note = pressure.Verdict switch
                    {
                        SweepPressureClassifier.Saturated =>
                            "The collection body cannot finish inside its cadence: relaunches are skipped every cycle and this server collects at a multiple of its configured interval, while each collector above correctly reads healthy from its own seat. The lever is capacity or placement (lighter or fewer scheduled collectors, a longer cadence, or a collector closer to the target), not collector repair.",
                        SweepPressureClassifier.AtRisk =>
                            "The collection body's average demand is close to its cadence; variance will intermittently push it over, skipping relaunches and stretching the delivered interval.",
                        _ => null
                    }
                },
                collectors = result
            }, McpHelpers.JsonOptions);
        }
        catch (Exception ex)
        {
            return McpHelpers.FormatError("get_collection_health", ex);
        }
    }
}
