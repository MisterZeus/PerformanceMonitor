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
        var state = new Dictionary<string, string> { [QueryStoreCollector.PlanWatermarkStateKeyPrefix + Db] = raw };
        var sql = LiveSql(Context(capturePlanXml: true, state: state));

        Assert.DoesNotContain("qsp.plan_id > ", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Watermark_Expired_RendersTheUnchangedQuery()
    {
        /* Bounded staleness is why the stamp is stored beside the id. Query Store can rewrite a plan's XML in
           place (memory grant feedback and friends) without issuing a new plan_id, and a permanent watermark
           would never look again. It also bounds the documented dormant-plan gap. */
        var stampedLongAgo = Now - QueryStoreCollector.PlanWatermarkRefreshAfter - TimeSpan.FromMinutes(1);

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
            [QueryStoreCollector.PlanWatermarkStateKeyPrefix + "alpha"] = "900000:" + Unix(Now),
        };
        var context = Context(capturePlanXml: true, state: state);

        Assert.Contains("AND qsp.plan_id > 900000",
            QueryStoreCollector.Instance.BuildPerItemQuery("alpha", context).Text, StringComparison.Ordinal);
        Assert.DoesNotContain("qsp.plan_id > ",
            QueryStoreCollector.Instance.BuildPerItemQuery("beta", context).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void StateKeys_IsNonEmpty_OrTheHostNeverLoadsTheWatermark()
    {
        /* The real keys are dynamic (one per database), so the declared key is the PREFIX. It has to be
           non-empty regardless: a definition declaring no state keys gets no state loaded, and the watermark
           would read absent forever — a silent no-op rather than a visible failure. */
        Assert.Contains(QueryStoreCollector.PlanWatermarkStateKeyPrefix, QueryStoreCollector.Instance.StateKeys);
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
           costs one repeated fetch and nothing else. */
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
        var longAgo = Now - QueryStoreCollector.PlanWatermarkRefreshAfter - TimeSpan.FromHours(1);
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
            [QueryStoreCollector.PlanWatermarkStateKeyPrefix + Db] =
                planId.ToString(CultureInfo.InvariantCulture) + ":" + Unix(stampedAt),
        };

    private static string Unix(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds()
            .ToString(CultureInfo.InvariantCulture);

    private static string? Written(CollectorContext context) =>
        context.PendingState.TryGetValue(QueryStoreCollector.PlanWatermarkStateKeyPrefix + Db, out var value)
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
}
