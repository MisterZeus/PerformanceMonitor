/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PerformanceMonitor.Collectors;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// #2164: the plan-XML watermark. 97% of the plan XML shipped in a three-hour fleet window was for plans the
/// store already held, and since drain is 94-97% of a pass and costs per-row LOB bytes, not fetching is worth
/// far more than fetching less.
///
/// <para>Driven entirely through the collector's PUBLIC surface — <c>BuildPerItemQuery</c>,
/// <c>BuildBackfillPerItemQuery</c>, <c>ReadItemAsync</c> — rather than reaching for the internal helpers, so
/// no production visibility is widened for the tests' benefit. It also makes the state format an explicit
/// pin: the stored string is written out literally here instead of being produced by the same formatter under
/// test, which would have agreed with itself no matter what it emitted.</para>
/// </summary>
public class QueryStorePlanWatermarkTests
{
    private const string Db = "probedb";
    private static readonly DateTime Now = new(2026, 8, 11, 12, 0, 0, DateTimeKind.Utc);

    /* ---------- the stored format, and what the query does with it ---------- */

    [Fact]
    public void Watermark_Fresh_NarrowsThePlanTextCase()
    {
        var sql = LiveSql(Context(capturePlanXml: true, state: Stored(900_000, Now)));

        Assert.Contains("AND qsp.plan_id > 900000", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Watermark_Absent_RendersTheUnchangedQuery()
    {
        /* Absent is what a first run, a restarted host and a broken store all look like, and all three must
           refetch rather than skip. The conservative path has to be byte-identical to the pre-change query. */
        var sql = LiveSql(Context(capturePlanXml: true, state: new Dictionary<string, string>()));

        Assert.DoesNotContain("qsp.plan_id > ", sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("900000")]                  /* no stamp */
    [InlineData("900000:")]                 /* empty stamp */
    [InlineData("notanumber:1786449600")]
    [InlineData("900000:notanumber")]
    [InlineData("900000:1786449600:extra")]
    [InlineData("0:1786449600")]            /* plan_id 0 is not a plan */
    [InlineData("-5:1786449600")]
    public void Watermark_Malformed_RendersTheUnchangedQuery(string raw)
    {
        /* Anything unparseable degrades to a full fetch. Trusting a partially parsed value would suppress
           plan XML based on a number nobody wrote. */
        var state = new Dictionary<string, string> { [QueryStorePlanXmlState.WatermarkKeyPrefix + Db] = raw };
        var sql = LiveSql(Context(capturePlanXml: true, state: state));

        Assert.DoesNotContain("qsp.plan_id > ", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Watermark_Expired_RendersTheUnchangedQuery()
    {
        /* Bounded staleness is why the stamp is stored beside the id. Query Store can rewrite a plan's XML in
           place (memory grant feedback and friends) without issuing a new plan_id, and a permanent watermark
           would never look again. It also bounds the documented dormant-plan gap. */
        var stampedLongAgo = Now - QueryStorePlanXmlState.RefreshAfter - TimeSpan.FromMinutes(1);

        var expired = LiveSql(Context(capturePlanXml: true, state: Stored(900_000, stampedLongAgo)));
        var stillFresh = LiveSql(Context(capturePlanXml: true, state: Stored(900_000, Now - TimeSpan.FromMinutes(1))));

        Assert.DoesNotContain("qsp.plan_id > ", expired, StringComparison.Ordinal);
        Assert.Contains("AND qsp.plan_id > 900000", stillFresh, StringComparison.Ordinal);
    }

    [Fact]
    public void Watermark_StampedInTheFuture_RendersTheUnchangedQuery()
    {
        /* A clock that moved backwards would otherwise pin the watermark for as long as the skew lasts. */
        var sql = LiveSql(Context(capturePlanXml: true, state: Stored(900_000, Now.AddDays(3))));

        Assert.DoesNotContain("qsp.plan_id > ", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Watermark_IsKeyedPerDatabase()
    {
        /* plan_id is monotonic WITHIN a database and means nothing across them, so one database's watermark
           must never be read for another. */
        var state = new Dictionary<string, string>
        {
            [QueryStorePlanXmlState.WatermarkKeyPrefix + "alpha"] = "900000:" + Unix(Now),
        };
        var context = Context(capturePlanXml: true, state: state);

        Assert.Contains("AND qsp.plan_id > 900000",
            QueryStoreCollector.Instance.BuildPerItemQuery("alpha", context).Text, StringComparison.Ordinal);
        Assert.DoesNotContain("qsp.plan_id > ",
            QueryStoreCollector.Instance.BuildPerItemQuery("beta", context).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDefinitionDeclaresNoStateKeys_TheHostOwnsThisState()
    {
        /* The watermark keys are one per DATABASE and only known at runtime, so the definition could not
           declare them even if it wanted to. More importantly it MUST NOT: a state-declaring definition is a
           two-host contract (CollectorStateContractTests pins default_trace_events as the only one), while
           this is host bookkeeping. The QueryStoreBackfillState seam — a separate state owner name — is what
           lets the host persist per-database state without the definition claiming any.

           The failure mode if this ever flips is silent, which is why it is pinned from both ends: a row
           written under the DEFINITION's name is never read back, so the watermark would resolve absent
           forever and collection would quietly keep paying full price. */
        Assert.Empty(QueryStoreCollector.Instance.StateKeys);
        Assert.NotEqual(QueryStorePlanXmlState.StateCollectorName, QueryStoreCollector.Instance.Name);
        Assert.Equal("query_store_plan_xml", QueryStorePlanXmlState.StateCollectorName);
        Assert.Equal("planwm:", QueryStorePlanXmlState.WatermarkKeyPrefix);
    }

    /* ---------- placement, which is the whole risk in the SQL change ---------- */

    [Fact]
    public void Watermark_SplicesInsideTheRowNumberGate_BeforeTHEN()
    {
        /* Inside the CASE's WHEN it narrows which plan gets its XML. Outside the CASE it would filter ROWS
           instead, deleting the runtime stats for every plan at or below the watermark. */
        var sql = LiveSql(Context(capturePlanXml: true, state: Stored(900_000, Now)));

        var when = sql.IndexOf("query_plan_text = CASE WHEN ROW_NUMBER()", StringComparison.Ordinal);
        var predicate = sql.IndexOf("AND qsp.plan_id > 900000", StringComparison.Ordinal);
        var then = sql.IndexOf("THEN CONVERT(nvarchar(max), qsp.query_plan)", StringComparison.Ordinal);

        Assert.True(when >= 0, "the plan-text CASE must still be there");
        Assert.True(predicate > when, "the predicate must be inside the CASE, not before it");
        Assert.True(predicate < then, "the predicate must be part of the WHEN, not the THEN");
    }

    [Fact]
    public void Watermark_NeverAppliesToBackfill()
    {
        /* The regression this pins was live in the first cut. The watermark tracks what the LIVE window has
           stored; backfill digs the other way, into intervals older than anything collected, whose rows
           reference plans compiled long ago and numbered BELOW the watermark. Applying it there suppresses
           essentially every plan the backfill exists to fetch — and silently, because runtime stats still
           ship, so a filled range looks complete while carrying no plan XML at all. */
        var context = Context(capturePlanXml: true, state: Stored(900_000, Now));

        var sql = QueryStoreCollector.Instance
            .BuildBackfillPerItemQuery(Db, context, Now.AddDays(-7), Now.AddDays(-1)).Text;

        Assert.DoesNotContain("qsp.plan_id > ", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Watermark_NeverAppliesWhenPlanCaptureIsOff()
    {
        /* Lite's shape. With no plan XML selected there is nothing to suppress, and the placeholder column
           must stay byte-identical to the no-plan form. */
        var sql = LiveSql(Context(capturePlanXml: false, state: Stored(900_000, Now)));

        Assert.DoesNotContain("qsp.plan_id > ", sql, StringComparison.Ordinal);
        Assert.Contains("query_plan_text = CONVERT(nvarchar(1), NULL),", sql, StringComparison.Ordinal);
    }

    /* ---------- write-back, driven through the real read loop ---------- */

    [Fact]
    public async Task WriteBack_NormalPass_AdvancesToTheHighestStoredPlanId()
    {
        var context = Context(capturePlanXml: true, state: new Dictionary<string, string>());
        await Read(context, Plan(10, xml: true), Plan(20, xml: true), Plan(30, xml: true));

        Assert.Equal("30:" + Unix(Now), Written(context));
    }

    [Fact]
    public async Task WriteBack_CountsOnlyPlansWhoseXmlActuallyShipped()
    {
        /* The ROW_NUMBER gate NULLs the XML on all but one interval per plan, so "seen" and "stored" differ
           on every real pass. Advancing on a plan whose XML was NULL would suppress that plan's XML from then
           on without ever having sent it. */
        var context = Context(capturePlanXml: true, state: new Dictionary<string, string>());
        await Read(context, Plan(10, xml: true), Plan(40, xml: false));

        Assert.Equal("10:" + Unix(Now), Written(context));
    }

    [Fact]
    public async Task WriteBack_BudgetCutPass_DoesNotAdvanceAtAll()
    {
        /* The second regression this pins. Rows ship ordered by last_execution_time, NOT plan_id, so a budget
           cut drops an arbitrary set of plan_ids off the tail of the window — including ids BELOW the highest
           one that stored. Advancing past them would suppress their XML on every later pass even though it
           never shipped once. The cut is already resumable on the time watermark, so declining to advance
           costs one repeated fetch and nothing else.

           THIS PINS A KNOWN-BROKEN BEHAVIOUR AS A BASELINE, NOT AS A DESIGN. Declining to advance is correct
           GIVEN time-ordered shipping, but 97.8% of production passes are budget-cut, so "does not advance at
           all" means the watermark never advances and the whole optimization is a measured no-op (#2210). The
           plan_id-ordered fetch removes the premise — a cut then truncates a suffix, making the advance safe —
           and this test is expected to be REPLACED at that point, not kept passing. Read it as a record of why
           the old shape could not work, and delete it with the shape. */
        var context = Context(capturePlanXml: true, state: new Dictionary<string, string>(), budgetOverride: 16);

        await Read(context, Plan(10, xml: true), Plan(20, xml: true), Plan(30, xml: true));

        Assert.True(context.PerItemTextBudgetExceeded,
            "the budget must actually have been cut, or this test proves nothing");
        Assert.Null(Written(context));
    }

    [Fact]
    public async Task WriteBack_QuietWindow_NeverMovesTheWatermarkBackward()
    {
        /* This is the case that killed the first design. A window whose newest-EXECUTING plan is older than
           the newest-COMPILED one is an ordinary quiet window, and on a steady workload it is most windows.
           The first cut read it as a Query Store reset and dropped the watermark, which would have refetched
           the whole catalog on nearly every pass — the exact cost being removed. */
        var context = Context(capturePlanXml: true, state: Stored(900_000, Now));
        await Read(context, Plan(800_000, xml: true), Plan(850_000, xml: true));

        Assert.Null(Written(context));
    }

    [Fact]
    public async Task WriteBack_Advance_CarriesTheOriginalStampForward_SoTheHorizonStillFires()
    {
        /* The stamp dates the last FULL fetch. If an advance re-stamped it to now, then any database that
           keeps compiling new plans would push its refresh horizon out forever — and those are the busy
           databases where a stale plan matters most. The bounded refresh would silently never happen. */
        var fetchedAt = Now - TimeSpan.FromHours(20);
        var context = Context(capturePlanXml: true, state: Stored(900_000, fetchedAt));

        await Read(context, Plan(950_000, xml: true));

        Assert.Equal("950000:" + Unix(fetchedAt), Written(context));
    }

    [Fact]
    public async Task WriteBack_AfterExpiry_StampsTheFullFetchAtNow()
    {
        /* The other half of the same rule: an expired watermark means THIS pass refetched everything, so it
           is the one case that legitimately re-dates the horizon. Without this the stamp would never move and
           every pass after the first expiry would be a full fetch. */
        var longAgo = Now - QueryStorePlanXmlState.RefreshAfter - TimeSpan.FromHours(1);
        var context = Context(capturePlanXml: true, state: Stored(900_000, longAgo));

        await Read(context, Plan(950_000, xml: true));

        Assert.Equal("950000:" + Unix(Now), Written(context));
    }

    [Fact]
    public async Task WriteBack_PlanCaptureOff_WritesNothing()
    {
        /* Lite reads the same rows with no XML. It must not leave a watermark behind that a plan-capturing
           host would later honor, having never shipped a single plan. */
        var context = Context(capturePlanXml: false, state: new Dictionary<string, string>());
        await Read(context, Plan(10, xml: true), Plan(30, xml: true));

        Assert.Null(Written(context));
    }

    /* ---------- helpers ---------- */

    private static Dictionary<string, string> Stored(long planId, DateTime stampedAt) =>
        new()
        {
            [QueryStorePlanXmlState.WatermarkKeyPrefix + Db] =
                planId.ToString(CultureInfo.InvariantCulture) + ":" + Unix(stampedAt),
        };

    private static string Unix(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds()
            .ToString(CultureInfo.InvariantCulture);

    private static string? Written(CollectorContext context) =>
        context.PendingState.TryGetValue(QueryStorePlanXmlState.WatermarkKeyPrefix + Db, out var value)
            ? value
            : null;

    private static string LiveSql(CollectorContext context) =>
        QueryStoreCollector.Instance.BuildPerItemQuery(Db, context).Text;

    private static CollectorContext Context(
        bool capturePlanXml,
        IReadOnlyDictionary<string, string> state,
        int? budgetOverride = null)
    {
        var context = new CollectorContext
        {
            ServerId = 1,
            ServerName = "probe",
            CollectionTime = Now,
            Deltas = new CollectorDeltaCalculator(),
            CapturePlanXml = capturePlanXml,
            State = state,
            TextByteBudgetOverride = budgetOverride,
        };
        context.CurrentDatabaseName = Db;
        return context;
    }

    private static (long PlanId, bool Xml) Plan(long planId, bool xml) => (planId, xml);

    private static async Task Read(CollectorContext context, params (long PlanId, bool Xml)[] plans)
    {
        using var reader = MakeReader(plans);
        var rows = new List<QueryStoreCollector.Row>();
        await QueryStoreCollector.Instance.ReadItemAsync(Db, reader, rows, context, CancellationToken.None);
    }

    /// <summary>
    /// A real <c>DbDataReader</c> over the collector's OWN payload shape, generated from
    /// <c>PayloadColumns</c> minus <c>database_name</c> (which the on-prem path takes from the enumerated
    /// item, not the reader). Generated rather than hand-listed so a column added to the collector cannot
    /// silently shift the ordinals the read loop depends on.
    /// </summary>
    private static DataTableReader MakeReader((long PlanId, bool Xml)[] plans)
    {
        var table = new DataTable("payload");
        var columns = QueryStoreCollector.Instance.PayloadColumns.Skip(1).ToList();

        foreach (var column in columns)
        {
            table.Columns.Add(column.Name, ClrType(column.Name, column.Type));
        }

        for (var i = 0; i < plans.Length; i++)
        {
            var row = table.NewRow();

            foreach (var column in columns)
            {
                row[column.Name] = column.Type switch
                {
                    CollectorColumnType.BigInt => 0L,
                    CollectorColumnType.Integer => 160,
                    CollectorColumnType.Boolean => false,
                    /* Distinct per row, so a budget cut's boundary tie group ends on the very next row. */
                    CollectorColumnType.Timestamp when ClrType(column.Name, column.Type) == typeof(DateTime)
                        => Now.AddMinutes(i),
                    CollectorColumnType.Timestamp => new DateTimeOffset(Now.AddMinutes(i), TimeSpan.Zero),
                    _ => "x",
                };
            }

            row["query_id"] = plans[i].PlanId * 10;
            row["plan_id"] = plans[i].PlanId;
            row["execution_count"] = 1L;
            /* Must not contain the self-query marker, or the read loop skips the row entirely. */
            row["query_text"] = "SELECT 1 FROM dbo.Whatever";
            row["query_plan_text"] = plans[i].Xml ? new string('p', 4096) : (object)DBNull.Value;

            table.Rows.Add(row);
        }

        var dataSet = new DataSet();
        dataSet.Tables.Add(table);
        return dataSet.CreateDataReader();
    }

    /// <summary>
    /// The provider types the read loop actually expects, which are NOT uniform across the timestamp
    /// columns: <c>first_execution_time</c> / <c>last_execution_time</c> come out of Query Store as
    /// <c>datetimeoffset</c> and are read as <c>DateTimeOffset</c>, while <c>interval_start_time_utc</c> is
    /// computed <c>datetime2</c> and read with <c>GetDateTime</c>. A harness that types all three the same
    /// way throws <c>InvalidCastException</c> inside the loop — which is how this was found.
    /// </summary>
    private static Type ClrType(string name, CollectorColumnType type) => type switch
    {
        CollectorColumnType.BigInt => typeof(long),
        CollectorColumnType.Integer => typeof(int),
        CollectorColumnType.Boolean => typeof(bool),
        CollectorColumnType.Timestamp =>
            name.Equals("interval_start_time_utc", StringComparison.Ordinal) ? typeof(DateTime) : typeof(DateTimeOffset),
        _ => typeof(string),
    };

    /* ---- #2210: the plan_id-ordered fetch policy. Pure functions, pinned like QueryStoreBackfillState
       .AdaptiveSpan, because the candidate window and the watermark advance are the two places this
       optimization can silently do nothing (attempt one) or silently lose plans (the ordering precondition). */

    /// <summary>
    /// The candidate window sits just past what the budget can actually ship, at every plan size the fleet
    /// ACTUALLY exhibits — per-quartile averages of 162 / 80 / 39 / 15 KB measured across 2,166 budget-cut
    /// passes. The point of the pin is that none of these clamp: if a real fleet plan size hit a bound, the
    /// bound would be doing the sizing instead of the measurement.
    /// </summary>
    [Theory]
    [InlineData(162, 114)]
    [InlineData(80, 231)]
    [InlineData(39, 473)]
    [InlineData(15, 1229)]
    public void CandidatePlanCount_SitsJustPastTheBudget_AtEveryMeasuredFleetPlanSize(int avgKb, int expected)
    {
        var k = QueryStorePlanXmlState.CandidatePlanCount(avgKb * 1024L, 12L * 1024 * 1024, out var clamped);

        Assert.Equal(expected, k);
        Assert.False(clamped, "a plan size the fleet actually shows must not hit a bound");

        /* Just past, not far past: the window is the coarse bound and the running byte total is the exact one,
           and every plan IN the window is decompressed to compute that total. */
        var actuallyFit = (12L * 1024 * 1024) / (avgKb * 1024L);
        Assert.InRange(k / (double)actuallyFit, 1.4, 1.6);
    }

    /// <summary>
    /// First contact assumes LARGE plans on purpose. The estimate is a divisor, so over-stating plan size
    /// yields a small window — and small is the safe direction: it only slows the watermark down, where too
    /// large decompresses a catalog to discover what fits, which is the trap the window exists to prevent.
    /// </summary>
    [Fact]
    public void CandidatePlanCount_WithNoPreviousPass_IsConservativelySmall()
    {
        var seed = QueryStorePlanXmlState.CandidatePlanCount(null, 12L * 1024 * 1024, out var clamped);
        var atLargestMeasured = QueryStorePlanXmlState.CandidatePlanCount(162 * 1024L, 12L * 1024 * 1024, out _);

        Assert.False(clamped);
        Assert.InRange(seed, atLargestMeasured - 10, atLargestMeasured + 10);
    }

    /// <summary>Bounds hold, and every clamp REPORTS itself — a window silently pinned at its ceiling reads
    /// exactly like one that fit, which is how a cap becomes invisible.</summary>
    [Theory]
    [InlineData(1, 12L * 1024 * 1024, QueryStorePlanXmlState.MaxCandidatePlans)]
    [InlineData(64 * 1024, 12L * 1024 * 1024, QueryStorePlanXmlState.MinCandidatePlans)]
    public void CandidatePlanCount_ClampsAndSaysSo(long avgKb, long budget, int expected)
    {
        var k = QueryStorePlanXmlState.CandidatePlanCount(avgKb * 1024L, budget, out var clamped);

        Assert.Equal(expected, k);
        Assert.True(clamped, "a clamped window must be reportable so the caller can log it");
    }

    /// <summary>
    /// `clamped` means a bound CHANGED the answer, not that the answer equals one. A window whose measured size
    /// lands naturally on a bound was sized by the measurement and needs no log line; reporting it as clamped is
    /// a false positive, and a caller that logs on it trains its reader to ignore the message.
    /// </summary>
    [Fact]
    public void CandidatePlanCount_LandingNaturallyOnABound_IsNotReportedAsClamped()
    {
        /* Budget chosen so budget/avg*margin is exactly MinCandidatePlans: 32 / 1.5 = 21.33 plans of 1 byte. */
        var exactlyTheFloor = (long)(QueryStorePlanXmlState.MinCandidatePlans / QueryStorePlanXmlState.CandidatePlanMargin);
        var k = QueryStorePlanXmlState.CandidatePlanCount(1, exactlyTheFloor, out var clamped);

        Assert.Equal(QueryStorePlanXmlState.MinCandidatePlans, k);
        Assert.False(clamped, "the measurement produced this value; no bound changed it");
    }

    /// <summary>A misconfigured budget floors the window rather than producing zero or a negative one.</summary>
    [Fact]
    public void CandidatePlanCount_WithNonPositiveBudget_FloorsAndReportsClamped()
    {
        var k = QueryStorePlanXmlState.CandidatePlanCount(160 * 1024L, 0, out var clamped);

        Assert.Equal(QueryStorePlanXmlState.MinCandidatePlans, k);
        Assert.True(clamped);
    }

    /// <summary>
    /// The estimator reproduces the measured fleet numbers from the same two inputs a pass already has, which
    /// is the whole reason no probe is needed: 12.1 MB over 78 plans is the q1 average, 12.3 MB over 828 is q4.
    /// </summary>
    [Theory]
    [InlineData(12.1, 78, 158)]
    [InlineData(12.3, 828, 15)]
    public void ObservedAvgPlanBytes_ReproducesTheMeasuredQuartiles(double shippedMb, int plans, int expectedKb)
    {
        var avg = QueryStorePlanXmlState.ObservedAvgPlanBytes((long)(shippedMb * 1024 * 1024), plans);

        Assert.NotNull(avg);
        Assert.Equal(expectedKb, avg!.Value / 1024);
    }

    /// <summary>A pass that shipped no plans teaches nothing about plan size and must leave the previous
    /// estimate standing rather than replace it with a fallback.</summary>
    [Fact]
    public void ObservedAvgPlanBytes_OnAQuietPass_IsNull()
    {
        Assert.Null(QueryStorePlanXmlState.ObservedAvgPlanBytes(0, 0));
        Assert.Null(QueryStorePlanXmlState.ObservedAvgPlanBytes(5_000, 0));
    }

    /// <summary>
    /// THE POINT OF THE WHOLE REDESIGN: a budget-cut pass still advances. Under plan_id-ordered shipping a cut
    /// truncates a SUFFIX, so the highest landed id is safe. The previous design shipped in
    /// last_execution_time order, where a cut left an arbitrary subset, no value was safe, and the guard that
    /// followed meant the watermark could not advance on 97.8% of passes.
    /// </summary>
    [Fact]
    public void AdvanceWatermark_OnABudgetCutPass_StillAdvances()
    {
        var cut = QueryStorePlanXmlState.AdvanceWatermark(100, new long[] { 101, 102 });

        Assert.Equal(102, cut.Watermark);
        Assert.True(cut.ArrivedInPlanIdOrder);
    }

    /// <summary>Never backward, and a quiet pass earns nothing: lowering the watermark refetches the catalog,
    /// and "no new plans this window" is an ordinary pass, not a reset.</summary>
    [Theory]
    [InlineData(new long[0], 100L)]
    [InlineData(new[] { 98L, 99L }, 100L)]
    [InlineData(new[] { 101L, 102L, 103L }, 103L)]
    [InlineData(new[] { 101L, 101L, 102L }, 102L)]
    public void AdvanceWatermark_NeverMovesBackward(long[] landed, long expected)
    {
        Assert.Equal(expected, QueryStorePlanXmlState.AdvanceWatermark(100, landed).Watermark);
    }

    /// <summary>
    /// A descent ABANDONS the advance rather than honouring the leading ascending run. Honouring it looks
    /// safer and is not: given {105, 101} it would advance to 105, and with ordering broken there is no basis
    /// for inferring that every SELECTED plan below 105 landed — so a plan whose XML never arrived would be
    /// suppressed until the refresh horizon. One lost pass of progress is the cheap side of that trade.
    /// </summary>
    [Theory]
    [InlineData(new[] { 101L, 102L, 99L, 105L })]
    [InlineData(new[] { 105L, 101L })]
    public void AdvanceWatermark_WhenOrderingIsViolated_RefusesToAdvance(long[] landed)
    {
        var refused = QueryStorePlanXmlState.AdvanceWatermark(100, landed);

        Assert.Equal(100, refused.Watermark);
        Assert.False(refused.ArrivedInPlanIdOrder,
            "the caller needs this to LOG the violation instead of just watching the watermark stop");
    }

    /// <summary>The ordering verdict rides along with the advance on the cases that are fine.</summary>
    [Theory]
    [InlineData(new[] { 101L, 102L, 103L })]
    [InlineData(new[] { 101L, 101L, 102L })]
    [InlineData(new[] { 7L })]
    [InlineData(new long[0])]
    public void AdvanceWatermark_AcceptsNonDescendingArrival(long[] landed)
    {
        Assert.True(QueryStorePlanXmlState.AdvanceWatermark(100, landed).ArrivedInPlanIdOrder);
    }
}
