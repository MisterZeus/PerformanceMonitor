using System.Collections.Generic;
using PerformanceMonitor.Analysis;
using Xunit;

namespace PerformanceMonitorDashboard.Tests;

/// <summary>
/// Tests the value-stated advice substrate (FactAdvice.Compose / PopulateStoryText /
/// GetComposedForFinding / Serialize-round-trip). The composer reads the server's ACTUAL settings
/// from the full fact set and states them — current MAXDOP, CTFP, cores — instead of generic
/// folklore, and the result is frozen into StoryText so read-back cards show the same numbers.
/// </summary>
public class FactAdviceComposeTests
{
    private static Dictionary<string, Fact> Facts(params Fact[] facts)
    {
        var d = new Dictionary<string, Fact>();
        foreach (var f in facts)
            d[f.Key] = f;
        return d;
    }

    private static Fact F(string key, double value) =>
        new() { Key = key, Source = "config", Value = value, Severity = 0.0 };

    private static Fact Hardware(int coresPerSocket) =>
        new()
        {
            Key = "SERVER_HARDWARE",
            Source = "config",
            Value = 1,
            Severity = 0,
            Metadata = new Dictionary<string, double> { ["cores_per_socket"] = coresPerSocket }
        };

    private static Fact FM(string key, double value, Dictionary<string, double> meta, string? obj = null) =>
        new() { Key = key, Source = "x", Value = value, Severity = 1, Metadata = meta, ObjectName = obj };

    // The audience invariant: composed advice must never contain MCP tool names or internal field names.
    private static readonly string[] ToolNameMarkers =
        { "get_", "audit_config", "parallel_only", "schema_table", "reconstructed_blocking_chains", "top_cpu_queries", "lock_mode_breakdown", "queries_at_spike", "best_plan_id" };

    private static void AssertComposedAndClean(AdviceBlock? a)
    {
        Assert.NotNull(a);
        var text = a!.Headline + " " + a.Investigation + " " + a.Remediation;
        foreach (var m in ToolNameMarkers)
            Assert.DoesNotContain(m, text);
    }

    // Regression guard for the routing bug: range-lock findings surface under the real LCK_M_R* keys
    // (kept individual by the collector), not "LCK_RANGE" — they must reach the composer (state wait
    // totals, no tool names), not fall through to the tool-named static block.
    [Fact]
    public void RangeLock_RealKey_IsComposed_StatesWaitTotals_NoToolNames()
    {
        var wait = new Dictionary<string, double>
        {
            ["wait_time_ms"] = 75000, ["waiting_tasks_count"] = 300, ["avg_ms_per_wait"] = 250,
            ["signal_wait_time_ms"] = 0, ["resource_wait_time_ms"] = 75000, ["period_duration_ms"] = 3600000,
        };
        var a = FactAdvice.Compose("LCK_M_RS_S", Facts(FM("LCK_M_RS_S", 0.05, wait)));
        AssertComposedAndClean(a);
        Assert.Contains("Key-range lock waits", a!.Investigation); // wait-totals prefix = composed, not static
        Assert.Contains("SERIALIZABLE", a.Investigation);
    }

    [Fact]
    public void BlockingChain_StatesDepthAndVictims_NoToolNames()
    {
        var meta = new Dictionary<string, double>
        {
            ["worst_chain_depth"] = 7, ["worst_chain_victim_count"] = 12, ["worst_apex_sleeping"] = 1,
            ["worst_chain_max_wait_ms"] = 48000, ["total_reconstructed_chains"] = 23,
        };
        var a = FactAdvice.Compose("BLOCKING_CHAIN", Facts(FM("BLOCKING_CHAIN", 7, meta)));
        AssertComposedAndClean(a);
        Assert.Contains("7", a!.Investigation);
        Assert.Contains("12", a.Investigation);
    }

    [Fact]
    public void AnomalyObjectGrowth_NamesTheObject_NoToolNames()
    {
        var meta = new Dictionary<string, double> { ["growth_mb"] = 6200, ["growth_pct"] = 44, ["current_mb"] = 20200 };
        var a = FactAdvice.Compose("ANOMALY_OBJECT_GROWTH", Facts(FM("ANOMALY_OBJECT_GROWTH", 6200, meta, obj: "dbo.Orders")));
        AssertComposedAndClean(a);
        Assert.Contains("dbo.Orders", a!.Headline);
    }

    [Fact]
    public void Sos_StatesSignalWaitShare_NoToolNames()
    {
        var wait = new Dictionary<string, double>
        {
            ["wait_time_ms"] = 600000, ["waiting_tasks_count"] = 5000, ["avg_ms_per_wait"] = 120,
            ["signal_wait_time_ms"] = 180000, ["resource_wait_time_ms"] = 420000, ["period_duration_ms"] = 3600000,
        };
        var a = FactAdvice.Compose("SOS_SCHEDULER_YIELD", Facts(FM("SOS_SCHEDULER_YIELD", 0.2, wait)));
        AssertComposedAndClean(a);
        Assert.Contains("signal wait", a!.Investigation);
    }

    [Fact]
    public void Cxpacket_Composed_NoToolNames()
    {
        var a = FactAdvice.Compose("CXPACKET", Facts(F("CONFIG_MAXDOP", 16), F("CONFIG_CTFP", 5), Hardware(8)));
        AssertComposedAndClean(a);
    }

    // ── THREADPOOL_PARALLEL: states the actual MAXDOP/CTFP, recommends the topology cap ──

    [Fact]
    public void ThreadpoolParallel_StatesCurrentMaxdopAndCtfp_AndRecommendsTopologyCap()
    {
        var advice = FactAdvice.Compose(
            "THREADPOOL_PARALLEL", Facts(F("CONFIG_MAXDOP", 16), F("CONFIG_CTFP", 5), Hardware(8)));

        Assert.NotNull(advice);
        var r = advice!.Remediation;
        Assert.Contains("MAXDOP is 16", r);
        Assert.Contains("cost threshold for parallelism is 5", r);
        Assert.Contains("raise cost threshold for parallelism to 50", r);
        Assert.Contains("lower MAXDOP from 16 to 8", r);
        // The whole point: no hedging about settings the engine collected.
        Assert.DoesNotContain("if those are already set", r);
    }

    [Fact]
    public void ThreadpoolParallel_AlreadyWithinGuidance_GuardsHarder()
    {
        // MAXDOP 8 / CTFP 50 are within topology guidance yet the pool still exhausted, so the
        // driver is concurrency volume — the workload-aware override guards harder.
        var advice = FactAdvice.Compose(
            "THREADPOOL_PARALLEL", Facts(F("CONFIG_MAXDOP", 8), F("CONFIG_CTFP", 50), Hardware(8)));

        var r = advice!.Remediation;
        Assert.Contains("MAXDOP is 8", r);
        Assert.Contains("already within topology guidance", r);
        Assert.Contains("MAXDOP from 8 to 4", r); // harder = rec/2
    }

    [Fact]
    public void ThreadpoolParallel_NoConfigFacts_FallsBackToStaticBlock()
    {
        var advice = FactAdvice.Compose("THREADPOOL_PARALLEL", Facts());
        Assert.Equal(FactAdvice.GetForFactKey("THREADPOOL_PARALLEL"), advice);
    }

    // ── CONFIG_MAXDOP: headline states the real value (the static block hard-coded "0") ──

    [Theory]
    [InlineData(0, "MAXDOP is 0")]
    [InlineData(1, "MAXDOP is 1")]
    [InlineData(16, "MAXDOP is 16")]
    public void ConfigMaxdop_HeadlineStatesActualValue(int maxdop, string expected)
    {
        var advice = FactAdvice.Compose("CONFIG_MAXDOP", Facts(F("CONFIG_MAXDOP", maxdop), Hardware(8)));
        Assert.Contains(expected, advice!.Headline);
    }

    [Fact]
    public void ConfigMaxdop_AboveGuidance_RecommendsLoweringToCores()
    {
        var advice = FactAdvice.Compose("CONFIG_MAXDOP", Facts(F("CONFIG_MAXDOP", 32), Hardware(8)));
        Assert.Contains("Lower MAXDOP from 32 to 8", advice!.Remediation);
    }

    // ── CONFIG_CTFP: states the real value ──

    [Fact]
    public void ConfigCtfp_StatesActualValue()
    {
        var advice = FactAdvice.Compose("CONFIG_CTFP", Facts(F("CONFIG_CTFP", 5)));
        Assert.Contains("Cost Threshold for Parallelism is 5", advice!.Headline);
        Assert.Contains("Raise it to 50", advice.Remediation);
    }

    // ── non-value keys pass through to the static block ──

    [Fact]
    public void Compose_NonValueKey_ReturnsStaticBlock()
    {
        Assert.Equal(FactAdvice.GetForFactKey("DEADLOCKS"), FactAdvice.Compose("DEADLOCKS", Facts()));
    }

    // ── serialize / read-back round-trip ──

    [Fact]
    public void SerializeForStoryText_RoundTrips()
    {
        var composed = FactAdvice.Compose(
            "THREADPOOL_PARALLEL", Facts(F("CONFIG_MAXDOP", 16), F("CONFIG_CTFP", 5), Hardware(8)));
        var json = FactAdvice.SerializeForStoryText(composed);

        Assert.StartsWith("{", json);
        var back = FactAdvice.TryReadStoryText(json);
        Assert.NotNull(back);
        Assert.Equal(composed!.Headline, back!.Headline);
        Assert.Equal(composed.Investigation, back.Investigation);
        Assert.Equal(composed.Remediation, back.Remediation);
    }

    [Fact]
    public void TryReadStoryText_LegacyOrEmpty_ReturnsNull()
    {
        Assert.Null(FactAdvice.TryReadStoryText(""));
        Assert.Null(FactAdvice.TryReadStoryText(null));
        Assert.Null(FactAdvice.TryReadStoryText("plain legacy text"));
    }

    // ── render entry point: prefer frozen StoryText, fall back to static ──

    [Fact]
    public void GetComposedForFinding_PrefersFrozenStoryText()
    {
        var custom = new AdviceBlock("Frozen headline", "Frozen investigation", "Frozen remediation");
        var finding = new AnalysisFinding
        {
            RootFactKey = "THREADPOOL_PARALLEL",
            StoryText = FactAdvice.SerializeForStoryText(custom)
        };

        var advice = FactAdvice.GetComposedForFinding(finding);
        Assert.Equal("Frozen headline", advice!.Headline);
        Assert.Equal("Frozen remediation", advice.Remediation);
    }

    [Fact]
    public void GetComposedForFinding_EmptyStoryText_FallsBackToStatic()
    {
        var finding = new AnalysisFinding { RootFactKey = "CONFIG_CTFP", StoryText = "" };
        Assert.Equal(FactAdvice.GetForFactKey("CONFIG_CTFP"), FactAdvice.GetComposedForFinding(finding));
    }

    // ── PopulateStoryText: freezes value-stated advice, skips absolution ──

    [Fact]
    public void PopulateStoryText_FreezesValueStatedAdvice_OnValueBearingStory()
    {
        var story = new AnalysisStory { RootFactKey = "THREADPOOL_PARALLEL", IsAbsolution = false };
        var facts = new List<Fact> { F("CONFIG_MAXDOP", 16), F("CONFIG_CTFP", 5), Hardware(8) };

        FactAdvice.PopulateStoryText(new[] { story }, facts);

        Assert.StartsWith("{", story.StoryText);
        var back = FactAdvice.TryReadStoryText(story.StoryText);
        Assert.Contains("MAXDOP is 16", back!.Remediation);
    }

    [Fact]
    public void PopulateStoryText_SkipsAbsolution()
    {
        var story = new AnalysisStory { RootFactKey = "server_health", IsAbsolution = true, StoryText = "" };
        FactAdvice.PopulateStoryText(new[] { story }, new List<Fact>());
        Assert.Equal("", story.StoryText);
    }

    // ── B1: parallelism value-gap blocks state the server's actual MAXDOP/CTFP ──

    [Theory]
    [InlineData("CXPACKET")]
    [InlineData("QUERY_HIGH_DOP")]
    [InlineData("THREADPOOL")]
    public void ParallelismBlocks_StateCurrentMaxdopCtfp(string key)
    {
        var facts = Facts(F("CONFIG_MAXDOP", 16), F("CONFIG_CTFP", 5), Hardware(8));
        var advice = FactAdvice.Compose(key, facts);
        Assert.Contains("MAXDOP is 16", advice!.Remediation);
        Assert.Contains("cost threshold for parallelism is 5", advice.Remediation);
        Assert.Contains("lower MAXDOP from 16 to 8", advice.Remediation);
    }

    [Fact]
    public void ParallelismBlocks_NoConfigFacts_FallBackToStatic()
    {
        Assert.Equal(FactAdvice.GetForFactKey("CXPACKET"), FactAdvice.Compose("CXPACKET", Facts()));
    }

    [Fact]
    public void Cxpacket_Investigation_DropsAuditConfigDeferral()
    {
        Assert.DoesNotContain("call `audit_config` to check CTFP and MAXDOP",
            FactAdvice.GetForFactKey("CXPACKET")!.Investigation);
    }

    // ── B2: memory value blocks state the actual cap / physical RAM instead of deferring ──

    [Fact]
    public void ConfigMaxMemory_StatesConcreteSuggestedCap()
    {
        var facts = Facts(F("CONFIG_MAX_MEMORY_MB", 2147483647), F("MEMORY_TOTAL_PHYSICAL_MB", 65536));
        var advice = FactAdvice.Compose("CONFIG_MAX_MEMORY_MB", facts);
        Assert.Contains("65,536 MB", advice!.Investigation);   // total physical RAM stated
        Assert.Contains("58,983 MB", advice.Remediation);       // suggested cap = total - max(4096, 10%)
        Assert.DoesNotContain("audit_config", advice.Investigation);
    }

    [Fact]
    public void ConfigMinMaxNarrow_StatesConfiguredMinAndMax()
    {
        var f = new Fact
        {
            Key = "CONFIG_MIN_MAX_MEMORY_NARROW", Source = "config", Value = 24000, Severity = 0.4,
            Metadata = new Dictionary<string, double> { ["min_memory_mb"] = 24000, ["max_memory_mb"] = 28672 }
        };
        var advice = FactAdvice.Compose("CONFIG_MIN_MAX_MEMORY_NARROW", Facts(f));
        Assert.Contains("24,000 MB", advice!.Investigation);
        Assert.Contains("28,672 MB", advice.Investigation);
        Assert.DoesNotContain("audit_config", advice.Investigation);
    }

    [Theory]
    [InlineData("PAGEIOLATCH_SH")]
    [InlineData("QUERY_SPILLS")]
    [InlineData("ANOMALY_MEMORY_PRESSURE")]
    public void MemoryWaitBlocks_StateCurrentCap_NoAuditConfigDeferral(string key)
    {
        var facts = Facts(F("CONFIG_MAX_MEMORY_MB", 28672), F("MEMORY_TOTAL_PHYSICAL_MB", 65536));
        var advice = FactAdvice.Compose(key, facts);
        Assert.Contains("max server memory is set to 28,672 MB", advice!.Remediation);
        Assert.DoesNotContain("audit_config", advice.Remediation);
    }

    [Fact]
    public void MemoryWaitBlocks_NoCapFact_FallBackToStatic()
    {
        Assert.Equal(FactAdvice.GetForFactKey("QUERY_SPILLS"), FactAdvice.Compose("QUERY_SPILLS", Facts()));
    }

    // ── B3: RCSI-deferral blocks state the RCSI-off count instead of deferring to audit_config ──

    private static Fact DbConfig(int rcsiOff) => new()
    {
        Key = "DB_CONFIG", Source = "config", Value = 1, Severity = 0.3,
        Metadata = new Dictionary<string, double> { ["rcsi_off_count"] = rcsiOff }
    };

    [Theory]
    [InlineData("BLOCKING_EVENTS")]
    [InlineData("DEADLOCKS")]
    public void RcsiBlocks_StateRcsiOffCount_WhenDbConfigCoFired(string key)
    {
        var advice = FactAdvice.Compose(key, Facts(DbConfig(9)));
        Assert.Contains("9 databases on this server currently have RCSI off", advice!.Remediation);
    }

    [Fact]
    public void RcsiBlocks_SingularPhrasing_ForOneDatabase()
    {
        var advice = FactAdvice.Compose("BLOCKING_EVENTS", Facts(DbConfig(1)));
        Assert.Contains("One database on this server currently has RCSI off", advice!.Remediation);
    }

    [Fact]
    public void RcsiBlocks_NoDbConfig_FallBackToStatic()
    {
        Assert.Equal(FactAdvice.GetForFactKey("DEADLOCKS"), FactAdvice.Compose("DEADLOCKS", Facts()));
    }

    [Fact]
    public void BlockingAndDeadlock_DropAuditConfigRcsiDeferral()
    {
        Assert.DoesNotContain("audit_config", FactAdvice.GetForFactKey("BLOCKING_EVENTS")!.Remediation);
        Assert.DoesNotContain("audit_config", FactAdvice.GetForFactKey("DEADLOCKS")!.Investigation);
    }
}
