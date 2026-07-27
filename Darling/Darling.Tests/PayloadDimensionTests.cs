/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using PerformanceMonitor.Collectors;
using PerformanceMonitor.Darling.Service;
using PerformanceMonitor.Darling.Storage;
using PerformanceMonitor.Darling.Viewer;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// Pins the #1767 payload dimensions — the change that stopped storing <c>query_text</c> /
/// <c>query_plan_xml</c> inline on every query_stats / procedure_stats row and moved the content into
/// hash-keyed dimension tables the fact rows reference by SHA-256 CONTENT digest.
///
/// <para>What is worth a test here is exactly what has no compiler and no runtime error behind it. The
/// diversion is keyed by payload column ORDINAL, so a column reorder or rename in the shared collector
/// definition would silently hash the wrong column or stop diverting entirely — and "stopped diverting"
/// looks like nothing at all: no exception, no warning, just the 94%-of-the-store defect quietly back.
/// So the ordinal is derived here from the definition rather than written down, and the COPY column list
/// is asserted POSITIONALLY against it: containment alone would pass a command whose digest column sat in
/// the wrong slot, which is the one failure binary COPY cannot detect.</para>
///
/// <para>The same reasoning drives the view pin: <c>v_query_stats</c> is generated from the collector
/// definition, and the test rebuilds the expected column list from that same definition, so the property
/// under test is "it cannot go stale", not "it currently says these 54 names".</para>
///
/// <para>Everything here runs without PostgreSQL. The store-side half — that the migration actually
/// creates the objects, that a COPY through the diversion really lands digests and resolves back through
/// the view, that the last_seen guard and the GC behave — is in <c>PayloadDimensionLiveTests</c>, gated on
/// DARLING_TEST_PG.</para>
/// </summary>
public sealed class PayloadDimensionTests
{
    /// <summary>The ordinal a named payload column occupies in a collector definition, or -1.</summary>
    private static int PayloadIndexOf(ICollectorSchemaInfo schema, string columnName)
    {
        for (var i = 0; i < schema.PayloadColumns.Count; i++)
        {
            if (string.Equals(schema.PayloadColumns[i].Name, columnName, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    // ── the diversion plan ──

    [Fact]
    public void DiversionPlanFor_QueryStats_ResolvesBothPayloadsAtTheirDefinitionOrdinals()
    {
        var schema = QueryStatsCollector.Instance;
        var plan = PayloadDimensions.DiversionPlanFor(schema);

        /* Expected ordinals are COMPUTED from the definition, never written down: the point of the test is
           that the plan TRACKS the definition. Hard-coding 36/37 would make this pass the exact reorder it
           exists to catch. */
        var textIndex = PayloadIndexOf(schema, "query_text");
        var planIndex = PayloadIndexOf(schema, "query_plan_xml");
        Assert.True(textIndex >= 0, "query_stats must declare a query_text payload column");
        Assert.True(planIndex >= 0, "query_stats must declare a query_plan_xml payload column");

        Assert.Equal(2, plan.Count);
        Assert.Equal(
            new[] { textIndex, planIndex }.Order().ToArray(),
            plan.Keys.Order().ToArray());

        Assert.Equal(PayloadDimensions.QueryTextDimTable, plan[textIndex].DimTable);
        Assert.Equal("query_text", plan[textIndex].PayloadColumn);
        Assert.Equal("query_text_digest", plan[textIndex].DigestColumn);

        Assert.Equal(PayloadDimensions.QueryPlanDimTable, plan[planIndex].DimTable);
        Assert.Equal("query_plan_xml", plan[planIndex].PayloadColumn);
        Assert.Equal("query_plan_digest", plan[planIndex].DigestColumn);
    }

    [Fact]
    public void DiversionPlanFor_ProcedureStats_DivertsOnlyThePlan_BecauseItHasNoQueryTextColumn()
    {
        var schema = ProcedureStatsCollector.Instance;
        var plan = PayloadDimensions.DiversionPlanFor(schema);

        /* procedure_stats' entire payload share is plan XML: the collector has never declared a query_text
           column, so a text dimension entry for it would throw at DiversionPlanFor rather than divert
           anything. Pin the absence, because that is the reason PayloadDimensions.All has two query_stats
           entries and only one for procedure_stats. */
        Assert.Equal(-1, PayloadIndexOf(schema, "query_text"));

        var planIndex = PayloadIndexOf(schema, "query_plan_xml");
        Assert.True(planIndex >= 0, "procedure_stats must declare a query_plan_xml payload column");

        var only = Assert.Single(plan);
        Assert.Equal(planIndex, only.Key);
        Assert.Equal(PayloadDimensions.QueryPlanDimTable, only.Value.DimTable);
        Assert.Equal("query_plan_digest", only.Value.DigestColumn);

        /* Both fact tables share ONE plan dimension — identical XML from two DMVs is the same content. */
        Assert.Equal(
            PayloadDimensions.DiversionPlanFor(QueryStatsCollector.Instance)
                .Values.Single(d => d.PayloadColumn == "query_plan_xml").DimTable,
            only.Value.DimTable);
    }

    [Fact]
    public void DiversionPlanFor_CollectorWithNoDimensions_IsEmpty_SoItKeepsThePreChangeInlinePath()
    {
        Assert.Empty(PayloadDimensions.DiversionPlanFor(WaitStatsCollector.Instance));
        Assert.Empty(PayloadDimensions.ForTable("wait_stats"));

        /* Exactly two fact tables divert; every other collector is untouched by #1767. */
        Assert.Equal(
            new[] { "procedure_stats", "query_stats" },
            PayloadDimensions.All.Select(d => d.TargetTable).Distinct().Order().ToArray());
    }

    [Fact]
    public void DiversionPlanFor_DeclaredColumnMissingFromTheDefinition_Throws_NamingTheColumnAndTable()
    {
        /* The silent failure this replaces: rename query_text in the shared definition and the diversion
           quietly stops matching, so every row stores its payload inline again with nothing anywhere
           reporting it. A schema that claims to be query_stats but has lost the column must throw. */
        var stale = new FakeSchema(
            "query_stats",
            QueryStatsCollector.Instance.PayloadColumns.Where(c => c.Name != "query_text").ToArray());

        var ex = Assert.Throws<InvalidOperationException>(() => PayloadDimensions.DiversionPlanFor(stale));

        Assert.Contains("query_text", ex.Message, StringComparison.Ordinal);
        Assert.Contains("query_stats", ex.Message, StringComparison.Ordinal);
        Assert.Contains(PayloadDimensions.QueryTextDimTable, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A collector schema surface built from an arbitrary column list — lets a test present a definition
    /// that has DRIFTED away from what <see cref="PayloadDimensions.All"/> declares, which no real
    /// collector can do while the guard is working.
    /// </summary>
    private sealed class FakeSchema : ICollectorSchemaInfo
    {
        public FakeSchema(string targetTable, IReadOnlyList<CollectorColumn> payloadColumns)
        {
            TargetTable = targetTable;
            PayloadColumns = payloadColumns;
        }

        public string Name => TargetTable;
        public string TargetTable { get; }
        public bool IncludesCollectionId => true;
        public string PrefixIdColumnName => "collection_id";
        public string PrefixTimeColumnName => "collection_time";
        public IReadOnlyList<CollectorColumn> PayloadColumns { get; }
        public bool AppliesTo(CollectorTargetInfo target) => true;
    }

    // ── the COPY column list ──

    /// <summary>
    /// Splits a generated COPY command into its column list, in order. The whole positional contract lives
    /// in this list, so the test reads it the way Postgres does rather than substring-matching it.
    /// </summary>
    private static string[] CopyColumns(string copyCommand)
    {
        var open = copyCommand.IndexOf('(', StringComparison.Ordinal);
        var close = copyCommand.IndexOf(") FROM STDIN (FORMAT BINARY)", StringComparison.Ordinal);
        Assert.True(open > 0 && close > open, $"unexpected COPY shape: {copyCommand}");

        return copyCommand[(open + 1)..close].Split(", ");
    }

    [Fact]
    public void CopyCommandFor_QueryStats_NamesTheDigestColumnsAtThePayloadColumnsOwnPositions()
    {
        var schema = QueryStatsCollector.Instance;
        var command = PgCollectorRowWriter.CopyCommandFor(schema);
        var columns = CopyColumns(command);

        /* The prefix the host writes before handing off to WritePayload: id (when the table has one),
           time, server_id, server_name. Payload ordinal i therefore lands at prefix + i. */
        var prefix = (schema.IncludesCollectionId ? 1 : 0) + 3;
        Assert.Equal(prefix + schema.PayloadColumns.Count, columns.Length);

        var textIndex = PayloadIndexOf(schema, "query_text");
        var planIndex = PayloadIndexOf(schema, "query_plan_xml");

        /* POSITIONAL, not containment. A binary COPY binds by the list's order; a command that named
           query_text_digest anywhere else would still contain the string and would still be accepted by
           this assertion if it only checked membership — and would then write a 32-byte digest into
           whatever column happened to sit at the payload's ordinal. */
        Assert.Equal("query_text_digest", columns[prefix + textIndex]);
        Assert.Equal("query_plan_digest", columns[prefix + planIndex]);

        /* Every other payload column keeps its own name at its own position. */
        for (var i = 0; i < schema.PayloadColumns.Count; i++)
        {
            if (i == textIndex || i == planIndex)
            {
                continue;
            }

            Assert.Equal(schema.PayloadColumns[i].Name, columns[prefix + i]);
        }

        /* The inline columns are not named at all — new rows leave them NULL. */
        Assert.DoesNotContain("query_text", columns);
        Assert.DoesNotContain("query_plan_xml", columns);
    }

    [Fact]
    public void CopyCommandFor_ProcedureStats_DivertsOnlyThePlanColumn()
    {
        var schema = ProcedureStatsCollector.Instance;
        var columns = CopyColumns(PgCollectorRowWriter.CopyCommandFor(schema));

        var prefix = (schema.IncludesCollectionId ? 1 : 0) + 3;
        Assert.Equal("query_plan_digest", columns[prefix + PayloadIndexOf(schema, "query_plan_xml")]);
        Assert.DoesNotContain("query_plan_xml", columns);
    }

    [Fact]
    public void CopyCommandFor_NonDivertingCollector_IsUnchangedByTheDimensions()
    {
        /* Every collector that declares no dimension keeps the pre-#1767 command verbatim, byte for byte. */
        Assert.Equal(
            "COPY wait_stats (collection_id, collection_time, server_id, server_name, wait_type, " +
            "waiting_tasks_count, wait_time_ms, signal_wait_time_ms, delta_waiting_tasks, delta_wait_time_ms, " +
            "delta_signal_wait_time_ms) FROM STDIN (FORMAT BINARY)",
            PgCollectorRowWriter.CopyCommandFor(WaitStatsCollector.Instance));
    }

    // ── the digest ──

    [Fact]
    public void Digest_IsContentAddressed_ThirtyTwoBytes_AndSeparatesLiteralVariants()
    {
        var digest = PayloadDimensions.Digest("SELECT 1;");
        Assert.Equal(32, digest.Length);                                     /* SHA-256 */
        Assert.Equal(digest, PayloadDimensions.Digest("SELECT 1;"));         /* same content, same key */
        Assert.NotEqual(digest, PayloadDimensions.Digest("SELECT 2;"));

        /* THE reason the key is a content digest and not one of SQL Server's own hash columns. These two
           statements share a query_hash — it is a query-SHAPE hash, so literal variants collapse onto one
           value — and a query_hash-keyed dimension with ON CONFLICT DO NOTHING would therefore pin ONE
           arbitrary literal variant forever and serve it as the text of every other. Every reader in this
           product shows the LATEST text; a shape key silently breaks that. The same argument rules out
           query_plan_hash for the plan dimension (a recompile with different sniffed parameters keeps the
           hash and changes the XML, so the dim would serve stale ParameterCompiledValues). */
        Assert.NotEqual(
            PayloadDimensions.Digest("SELECT * FROM t WHERE id = 1"),
            PayloadDimensions.Digest("SELECT * FROM t WHERE id = 2"));

        /* Empty content is still content — it hashes, it does not throw. Only null is rejected. */
        Assert.Equal(32, PayloadDimensions.Digest(string.Empty).Length);
        Assert.Throws<ArgumentNullException>(() => PayloadDimensions.Digest(null!));
    }

    // ── the per-batch accumulator ──

    [Fact]
    public void Batch_CollapsesRepeatContent_KeepsDistinctContentApart_AndReportsEmptiness()
    {
        var batch = new PayloadDimensionBatch();
        Assert.True(batch.IsEmpty);
        Assert.Equal(0, batch.DistinctCount(PayloadDimensions.QueryPlanDimTable));

        const string PlanXml = "<ShowPlanXML><StmtSimple/></ShowPlanXML>";
        for (var i = 0; i < 3; i++)
        {
            batch.Add(PayloadDimensions.QueryPlanDimTable, PayloadDimensions.Digest(PlanXml), PlanXml);
        }

        Assert.False(batch.IsEmpty);

        /* One distinct plan out of three sightings. This is not only size: Postgres raises 21000 ("ON
           CONFLICT DO UPDATE command cannot affect row a second time") if one statement presents the same
           key twice, so a batch that failed to collapse would fail the upsert outright. */
        Assert.Equal(1, batch.DistinctCount(PayloadDimensions.QueryPlanDimTable));

        const string OtherPlan = "<ShowPlanXML><StmtCursor/></ShowPlanXML>";
        batch.Add(PayloadDimensions.QueryPlanDimTable, PayloadDimensions.Digest(OtherPlan), OtherPlan);
        Assert.Equal(2, batch.DistinctCount(PayloadDimensions.QueryPlanDimTable));

        /* Dimension tables are independent buckets. */
        Assert.Equal(0, batch.DistinctCount(PayloadDimensions.QueryTextDimTable));
        batch.Add(PayloadDimensions.QueryTextDimTable, PayloadDimensions.Digest("SELECT 1;"), "SELECT 1;");
        Assert.Equal(1, batch.DistinctCount(PayloadDimensions.QueryTextDimTable));
        Assert.Equal(2, batch.DistinctCount(PayloadDimensions.QueryPlanDimTable));
    }

    [Fact]
    public void Batch_DedupsAcrossSeparateByteArrayInstances_TheReferenceEqualityTrapHexKeyingAvoids()
    {
        /* Each row hashes its own payload, so two rows sharing a plan arrive with two DISTINCT byte[]
           instances holding identical bytes. A Dictionary<byte[], _> hashes arrays by REFERENCE, so it
           would treat these as different keys, never dedup, and hand the upsert a duplicate conflict key —
           a 21000 on essentially every batch. The hex-string keying is what makes this pass. */
        const string PlanXml = "<ShowPlanXML><StmtSimple/></ShowPlanXML>";
        var first = PayloadDimensions.Digest(PlanXml);
        var second = PayloadDimensions.Digest(PlanXml);

        Assert.NotSame(first, second);
        Assert.Equal(first, second);

        var batch = new PayloadDimensionBatch();
        batch.Add(PayloadDimensions.QueryPlanDimTable, first, PlanXml);
        batch.Add(PayloadDimensions.QueryPlanDimTable, second, PlanXml);

        Assert.Equal(1, batch.DistinctCount(PayloadDimensions.QueryPlanDimTable));
    }

    [Fact]
    public void Batch_ToArrays_AreIndexAligned_AndEmptyForATableWithNothingDiverted()
    {
        var batch = new PayloadDimensionBatch();
        var payloads = new[] { "SELECT 1;", "SELECT 2;", "SELECT 3;" };
        foreach (var payload in payloads)
        {
            batch.Add(PayloadDimensions.QueryTextDimTable, PayloadDimensions.Digest(payload), payload);
        }

        var (digests, texts) = batch.ToArrays(PayloadDimensions.QueryTextDimTable);

        /* unnest($1::bytea[], $2::text[]) pairs the arrays BY POSITION, so a misalignment would store each
           payload under another payload's digest — content-addressed storage that is not, silently. */
        Assert.Equal(digests.Length, texts.Length);
        Assert.Equal(payloads.Length, digests.Length);
        for (var i = 0; i < digests.Length; i++)
        {
            Assert.Equal(PayloadDimensions.Digest(texts[i]), digests[i]);
        }

        Assert.Equal(payloads.Order().ToArray(), texts.Order().ToArray());

        /* A table nothing was diverted into yields empty arrays, not null — the flush skips it. */
        var (noDigests, noPayloads) = batch.ToArrays(PayloadDimensions.QueryPlanDimTable);
        Assert.Empty(noDigests);
        Assert.Empty(noPayloads);
    }

    // ── the SQL the dimensions are written and swept with ──

    [Fact]
    public void UpsertSql_IsOneUnnestStatement_WithTheOnConflictStalenessGuard()
    {
        var sql = PayloadDimensions.UpsertSql(PayloadDimensions.QueryTextDimTable);

        /* ON CONFLICT is the entire dedup mechanism: the second and every later sighting of the same
           content writes no new row. */
        Assert.Contains("ON CONFLICT (digest) DO UPDATE", sql, StringComparison.Ordinal);

        /* The churn guard. Without it, every referenced dim row takes an UPDATE every collection cycle —
           a dead tuple per row per minute — to maintain a watermark whose only consumer (the GC) has a
           multi-day horizon. */
        Assert.Contains("INTERVAL '1 hour'", sql, StringComparison.Ordinal);

        /* ONE statement with array parameters: an Npgsql batch mixing multiple statements with positional
           parameters fails SILENTLY, and a 200-row batch would otherwise cost 200 round trips. */
        Assert.Contains("unnest($1::bytea[], $2::text[])", sql, StringComparison.Ordinal);

        Assert.Equal(
            "INSERT INTO query_text_dim (digest, query_text, last_seen)\n" +
            "SELECT u.digest, u.payload, $3\n" +
            "FROM unnest($1::bytea[], $2::text[]) AS u(digest, payload)\n" +
            "ON CONFLICT (digest) DO UPDATE SET last_seen = EXCLUDED.last_seen\n" +
            "WHERE query_text_dim.last_seen < EXCLUDED.last_seen - INTERVAL '1 hour'",
            sql);

        /* The plan dimension is the same statement over its own content column. */
        Assert.Contains(
            "INSERT INTO query_plan_dim (digest, query_plan_xml, last_seen)",
            PayloadDimensions.UpsertSql(PayloadDimensions.QueryPlanDimTable),
            StringComparison.Ordinal);

        Assert.Throws<ArgumentOutOfRangeException>(() => PayloadDimensions.UpsertSql("not_a_dim"));
    }

    [Fact]
    public void DimensionGc_ReusesTheTimeSlicedDelete_NotAnAntiJoinAndNotAnUnboundedStatement()
    {
        /* Two properties, both load-bearing. It is a last_seen range delete rather than the obvious
           sweep (delete dim rows no live fact references), which would be an anti-join against two
           hypertables per dim row -- affordable only because the write path stamps last_seen on every
           cycle that references the digest. And it goes through the SAME time-sliced DELETE every
           sibling purge uses, so the first sweep after an upgrade -- a whole retention window of
           expired content -- is bounded work per statement instead of one transaction holding a lock
           and generating WAL for the entire backlog. */
        foreach (var dimTable in PayloadDimensions.DimTables)
        {
            var sql = DarlingRetention.TimeSlicedDeleteSql(dimTable, PayloadDimensions.LastSeenColumn);

            Assert.StartsWith($"DELETE FROM {dimTable} WHERE last_seen < $1", sql, StringComparison.Ordinal);
            Assert.Contains($"min(last_seen) FROM {dimTable}", sql, StringComparison.Ordinal);
            Assert.Contains("INTERVAL", sql, StringComparison.Ordinal);

            /* Never the anti-join: no dim GC statement may reference a fact table. */
            Assert.DoesNotContain("query_stats", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("procedure_stats", sql, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CreateDimTable_IsAPlainTableKeyedByDigest_WithALastSeenIndex()
    {
        /* Plain tables, deliberately NOT hypertables: no time axis to partition on, and a hypertable's
           unique constraint must include the partitioning column, which a content key cannot satisfy. */
        Assert.Equal(
            "CREATE TABLE IF NOT EXISTS query_plan_dim (\n" +
            "    digest bytea NOT NULL PRIMARY KEY,\n" +
            "    query_plan_xml text NOT NULL,\n" +
            "    last_seen timestamp NOT NULL\n" +
            ");\n" +
            "CREATE INDEX IF NOT EXISTS idx_query_plan_dim_last_seen ON query_plan_dim(last_seen);",
            PayloadDimensions.CreateDimTable(PayloadDimensions.QueryPlanDimTable));

        Assert.Equal("query_text", PayloadDimensions.PayloadColumnOf(PayloadDimensions.QueryTextDimTable));
        Assert.Equal("query_plan_xml", PayloadDimensions.PayloadColumnOf(PayloadDimensions.QueryPlanDimTable));
        Assert.Throws<ArgumentOutOfRangeException>(() => PayloadDimensions.PayloadColumnOf("query_stats"));
    }

    // ── the resolving view ──

    /// <summary>
    /// The output name of one select-list entry: the alias when the entry is a COALESCE, otherwise the
    /// column name after the fact alias.
    /// </summary>
    private static string OutputName(string selectEntry)
    {
        var asIndex = selectEntry.LastIndexOf(" AS ", StringComparison.Ordinal);
        if (asIndex >= 0)
        {
            return selectEntry[(asIndex + 4)..];
        }

        var dot = selectEntry.LastIndexOf('.');
        return dot >= 0 ? selectEntry[(dot + 1)..] : selectEntry;
    }

    /// <summary>The generated view's select list, in order, trimmed.</summary>
    private static string[] ViewSelectList(string view)
    {
        const string SelectMarker = "SELECT\n";
        const string FromMarker = "\nFROM query_stats AS f";

        var start = view.IndexOf(SelectMarker, StringComparison.Ordinal);
        var end = view.IndexOf(FromMarker, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "unexpected resolving-view shape");

        return view[(start + SelectMarker.Length)..end]
            .Split(",\n")
            .Select(entry => entry.Trim())
            .ToArray();
    }

    /// <summary>The column names of a generated CREATE TABLE, in order.</summary>
    private static string[] CreateTableColumns(string ddl)
    {
        var start = ddl.IndexOf("(\n", StringComparison.Ordinal);
        var end = ddl.IndexOf("\n);", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "unexpected CREATE TABLE shape");

        return ddl[(start + 2)..end]
            .Split('\n')
            .Select(line => line.Trim().TrimEnd(',').Split(' ')[0])
            .ToArray();
    }

    [Fact]
    public void ResolvingView_CoalescesBothPayloads_AndLeftJoinsBothDimensionsOnDigest()
    {
        var view = PgSchemaGenerator.GenerateQueryStatsResolvingView();

        /* The transition contract in one line each: the inline column for rows written before the
           dimensions existed, the dimension row for everything written since. Resolving it HERE fixes
           every reader that goes through the view and makes future readers correct by default — the real
           hazard being that a reader selecting the raw column does not error, it returns NULL. */
        Assert.Contains("COALESCE(f.query_text, qtd.query_text) AS query_text", view, StringComparison.Ordinal);
        Assert.Contains("COALESCE(f.query_plan_xml, qpd.query_plan_xml) AS query_plan_xml", view, StringComparison.Ordinal);

        Assert.Contains("CREATE OR REPLACE VIEW v_query_stats AS", view, StringComparison.Ordinal);
        Assert.Contains("FROM query_stats AS f", view, StringComparison.Ordinal);
        Assert.Contains("LEFT JOIN query_text_dim AS qtd ON qtd.digest = f.query_text_digest", view, StringComparison.Ordinal);
        Assert.Contains("LEFT JOIN query_plan_dim AS qpd ON qpd.digest = f.query_plan_digest", view, StringComparison.Ordinal);

        /* LEFT, not INNER: a row with no digest and no inline value must still appear. */
        Assert.DoesNotContain("INNER JOIN", view, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolvingView_ProjectsEveryTableColumnInOrder_ThenTheTwoDigestsLast()
    {
        var view = PgSchemaGenerator.GenerateQueryStatsResolvingView();
        var selectList = ViewSelectList(view);

        /* Both sides derived from the collector definition: the property under test is that the view
           CANNOT go stale, not that it currently lists a particular set of names. A payload column added
           to the collector appears in both lists automatically. */
        var tableColumns = CreateTableColumns(PgSchemaGenerator.CreateTable(QueryStatsCollector.Instance));
        var digestColumns = PayloadDimensions.ForTable("query_stats").Select(d => d.DigestColumn).ToArray();

        Assert.Equal(tableColumns.Length + digestColumns.Length, selectList.Length);
        Assert.Equal(
            tableColumns,
            selectList.Take(tableColumns.Length).Select(OutputName).ToArray());

        /* The digests are APPENDED. CREATE OR REPLACE VIEW permits only additions at the end — same names,
           same types, same order — so a digest inserted next to its payload column would make V38 fail to
           replace the previous SELECT * definition on every upgraded store. */
        Assert.Equal(
            digestColumns.Select(c => "    f." + c).ToArray(),
            selectList.Skip(tableColumns.Length).Select(entry => "    " + entry).ToArray());
    }

    // ── the migration ──

    [Fact]
    public void V38_CreatesBothDimensions_AddsAllThreeDigestColumns_AndOrdersTablesBeforeTheView()
    {
        var v38 = PgSchemaGenerator.GenerateV38PayloadDimensions();

        foreach (var dimTable in PayloadDimensions.DimTables)
        {
            Assert.Contains($"CREATE TABLE IF NOT EXISTS {dimTable} (", v38, StringComparison.Ordinal);
            Assert.Contains(PayloadDimensions.CreateDimTable(dimTable), v38, StringComparison.Ordinal);
        }

        /* ADD COLUMN IF NOT EXISTS, nullable: the migration is metadata-only, so it never rewrites the
           ~234 GB of existing payload (the peak-disk-before-relief trap this design exists to avoid). The
           digest columns are NOT in the collector definitions — they are a Darling storage concern and
           Lite must not grow them — so a fresh V1 store does not already have them and this ALTER is the
           only thing that creates them. */
        foreach (var dimension in PayloadDimensions.All)
        {
            Assert.Contains(
                $"ALTER TABLE {dimension.TargetTable} ADD COLUMN IF NOT EXISTS {dimension.DigestColumn} bytea;",
                v38,
                StringComparison.Ordinal);
        }

        Assert.Equal(3, PayloadDimensions.All.Count);

        /* Dependency order inside the single migration body: the view LEFT JOINs both dimensions, so both
           CREATE TABLEs must precede the CREATE OR REPLACE VIEW or V38 fails on relation-does-not-exist. */
        var viewIndex = v38.IndexOf("CREATE OR REPLACE VIEW v_query_stats", StringComparison.Ordinal);
        Assert.True(viewIndex > 0, "V38 must rebuild v_query_stats");
        foreach (var dimTable in PayloadDimensions.DimTables)
        {
            var tableIndex = v38.IndexOf($"CREATE TABLE IF NOT EXISTS {dimTable} (", StringComparison.Ordinal);
            Assert.True(
                tableIndex >= 0 && tableIndex < viewIndex,
                $"{dimTable} must be created before the view that joins it");
        }

        /* The view body is the generated one, not a second hand-written copy that could drift. */
        Assert.Contains(PgSchemaGenerator.GenerateQueryStatsResolvingView(), v38, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLastMigrationDefiningTheView_IsTheResolvingOne_NotAPassthroughReExpansion()
    {
        /* v_query_stats is defined THREE times across the ladder: V4 created it as a bare
           `SELECT * FROM query_stats`, V14 re-expanded every passthrough view to pick up columns added
           since, and V38 replaced it with the resolving definition. Whichever comes LAST is the one a
           migrated store actually has.

           This is the tripwire for a specific, silent regression. v_query_stats is still a member of
           PgSchemaGenerator.AllPassthroughViews — the set GenerateV14RefreshViews() re-expands as
           SELECT * — and "add a column, then refresh the views" is this file's established idiom. A future
           migration following it would overwrite the COALESCE with a passthrough, and nothing would fail:
           every text and plan read would simply return NULL for rows written since #1767, which is
           indistinguishable from a quiet collection outage. Any new migration that redefines this view
           must define it as the RESOLVING one. */
        var definers = PgMigrations.Scripts
            .Where(m => m.Sql.Contains("VIEW v_query_stats AS", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(definers);
        Assert.Equal(38, definers[^1].Version);
        Assert.Contains(
            "COALESCE(f.query_text, qtd.query_text) AS query_text",
            definers[^1].Sql,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "VIEW v_query_stats AS SELECT * FROM query_stats",
            definers[^1].Sql,
            StringComparison.Ordinal);
    }

    [Fact]
    public void V38_BodyIsTheGeneratedScript_AndTheViewerProbeGatesOnTheDimensions()
    {
        /* The ladder itself — version 38 registered last, under that name, with StorageVersion in step — is
           pinned by DarlingObservabilityTests, where every other migration's identity lives. What belongs
           here is that the registered body IS the generator's output (not a hand-copied twin that could
           drift from the collector definition it is derived from) and that the viewer can see V38. */
        Assert.Equal(
            PgSchemaGenerator.GenerateV38PayloadDimensions(),
            PgMigrations.Scripts.Single(m => m.Version == 38).Sql);

        /* The viewer MUST gate on V38: from here the collectors stop writing query_text/query_plan_xml
           inline, so a pre-V38 viewer opened against a V38 store would read NULL for every new row's text
           and plan — no error, just a product that quietly shows nothing. */
        Assert.Equal(StorageVersion.SchemaVersion, ViewerDataService.RequiredStoreSchemaVersion);
        Assert.Contains("query_plan_dim", ViewerDataService.StoreSchemaProbeSql, StringComparison.Ordinal);
    }

    // ── the positional COPY contract EndPayload asserts ──

    [Fact]
    public void EndPayload_ThrowsWhenTheCollectorWroteTheWrongNumberOfPayloadValues()
    {
        /* Without this check a WritePayload/PayloadColumns drift shifts every later column by one and
           surfaces as an opaque Npgsql type error several columns downstream of the mistake — and with a
           diversion plan active it would hash the WRONG column and write a digest where the store expects
           text. Checked for EVERY collector, not just the diverted ones. */
        var writer = new PgCollectorRowWriter();
        writer.BeginPayload();

        var ex = Assert.Throws<InvalidOperationException>(
            () => writer.EndPayload(QueryStatsCollector.Instance.PayloadColumns.Count));

        Assert.Contains("payload values", ex.Message, StringComparison.Ordinal);
        Assert.Contains("PayloadColumns", ex.Message, StringComparison.Ordinal);
        Assert.Contains(
            QueryStatsCollector.Instance.PayloadColumns.Count.ToString(CultureInfo.InvariantCulture),
            ex.Message,
            StringComparison.Ordinal);

        /* A matching count is silent, and BeginPayload restarts the counter after the prefix columns the
           host writes through this same writer. */
        writer.BeginPayload();
        writer.EndPayload(0);
    }

    // ── the silent-NULL class this change created ──

    /// <summary>
    /// The residual risk #1767 carries, guarded where it is actually introduced: SQL that selects
    /// <c>query_text</c> or <c>query_plan_xml</c> straight off the BARE <c>query_stats</c> /
    /// <c>procedure_stats</c> tables.
    ///
    /// <para><b>Why it needs a guard.</b> That query does not error. The columns still exist — the
    /// migration is zero-rewrite and deliberately kept them — so Postgres returns NULL for every row
    /// written since #1767 and the reader renders an empty text pane, an empty plan, a disabled download
    /// button. That looks EXACTLY like a collection outage, which is the wrong place to start looking, and
    /// the SQL is the last place anyone checks. Nothing else in this repo can see it: the compiler never
    /// reads SQL text, and a behavioral test only trips if it happens to seed a dimension-written row
    /// rather than an inline one — and inline rows are what the transition period is full of.</para>
    ///
    /// <para><b>The rule.</b> A SQL literal that reads a bare fact table AND names a payload column must
    /// also show that it knows where that payload now lives, in one of three ways, each of which is a real
    /// correct pattern already in this tree: it names a digest column (it joins the dimension itself), it
    /// wraps a payload column in <c>COALESCE</c> (it resolves inline-vs-dimension), or it reads
    /// <c>v_query_stats</c> (the resolving view does it for the statement). Every current site satisfies
    /// one of the three, with no allowlist — an allowlist here would be self-defeating, because the next
    /// entry added to it is exactly the bug.</para>
    ///
    /// <para><b>What it deliberately cannot see.</b> This is a lexical guard, not a SQL parser. It asks
    /// whether the STATEMENT shows awareness, not whether each individual column reference is bound to a
    /// resolving relation, so a literal that legitimately reads the base table for an aggregate and the
    /// view for the text satisfies it — and would still satisfy it if a later edit moved the text read onto
    /// the base table. That is an accepted limit, not an oversight: the failure this exists to catch is a
    /// whole statement written against the base table by someone who did not know the dimensions exist.</para>
    /// </summary>
    [Fact]
    public void NoDarlingSqlReadsAPayloadColumnStraightOffABareFactTable()
    {
        var root = FindRepoRoot();

        /* FAIL rather than skip when the tree cannot be found — a guard that silently skips is a guard
           that silently stopped guarding. Same reasoning (and same walk-up) as DocCommentHygieneTests. */
        Assert.True(root is not null, RepoRootNotFound);

        var darling = Path.Combine(root!, "Darling");
        Assert.True(Directory.Exists(darling),
            $"Expected the Darling source tree at '{darling}'. This guard scans it, so it cannot run " +
            "without it.");

        var offenders = new List<string>();
        var judged = 0;

        foreach (var file in Directory.EnumerateFiles(darling, "*.cs", SearchOption.AllDirectories))
        {
            /* Build output, and the test project itself: tests legitimately seed inline payloads (that is
               how the pre-#1767 transition rows get written) and legitimately assert on raw column names. */
            if (file.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase)
                || file.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase)
                || file.Contains(@"\Darling.Tests\", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var source = File.ReadAllText(file);
            foreach (var (literal, index) in StringLiterals(source))
            {
                if (!PayloadColumns.Any(column => ContainsWord(literal, column)))
                {
                    continue;
                }

                var bareRead = FactTables
                    .Select(table => FromOrJoinOf(literal, table))
                    .FirstOrDefault(match => match is not null);

                if (bareRead is null)
                {
                    continue;
                }

                judged++;

                if (literal.Contains("query_text_digest", StringComparison.Ordinal)
                    || literal.Contains("query_plan_digest", StringComparison.Ordinal)
                    || CoalescesAPayloadColumn(literal)
                    || FromOrJoinOf(literal, ResolvingView) is not null)
                {
                    continue;
                }

                var line = source.AsSpan(0, index + bareRead.Value.Index).Count('\n') + 1;
                offenders.Add($"{Path.GetRelativePath(root!, file)}:{line}  ->  \"{bareRead.Value.Text}\"");
            }
        }

        /* A rule that matched nothing is not a passing rule, it is a broken scanner reporting a clean
           tree forever. The SQL in this repo is RAW string literals ("""), not the @"..." form, so a
           scanner that quietly stopped recognising them would look green on every run. */
        Assert.True(judged > 0,
            "The payload-column guard judged ZERO literals, which means the scanner found no SQL at all — " +
            "not that the tree is clean. Fix the literal scanner (raw string literals are the form this " +
            "repo's SQL is written in) before trusting a green run.");

        Assert.True(offenders.Count == 0,
            "SQL reads a payload column straight off a bare fact table. Since #1767 the collectors write " +
            "query_text / query_plan_xml as NULL and put the content in query_text_dim / query_plan_dim, " +
            "keyed by a content digest on the fact row.\n\n" +
            "This does NOT error. It returns NULL for every row collected since #1767, so the product " +
            "shows an empty query text, an empty plan, a dead Download button — which reads as a " +
            "collection outage, not as a bug, and sends the investigation to the collector instead of to " +
            "the query.\n\n" +
            "Two correct fixes:\n" +
            "  1. Read v_query_stats instead of query_stats — it COALESCEs the inline column with the " +
            "dimension join and is correct for both pre- and post-#1767 rows.\n" +
            "  2. Where there is no resolving view (procedure_stats has none), LEFT JOIN the dimension " +
            "explicitly on its digest column and select COALESCE(f.<payload>, d.<payload>) — and move any " +
            "'<payload> IS NOT NULL' guard onto the COALESCED expression, or it discards every new row " +
            "before the join can resolve it.\n\n" +
            string.Join("\n", offenders));
    }

    // ── the transaction the atomicity argument depends on ──

    /// <summary>
    /// That <c>DarlingCollectorRunner.WriteBatchAsync</c> flushes the dimensions INSIDE the batch's
    /// transaction, before the commit.
    ///
    /// <para>Pinned by parsing the source because the writer is private and reachable only through a live
    /// SQL Server connection, so the live test that proves the property
    /// (<c>PayloadDimensionLiveTests.AtomicVisibility_NoOtherSessionEverSeesAFactRowWhoseDigestHasNoDimRow</c>)
    /// necessarily MIRRORS this ordering rather than calling it — which means the live test alone would
    /// stay green if production's ordering changed underneath it. This is the seam between them.</para>
    ///
    /// <para>Reordering these three calls compiles, passes every unit test, and passes the live test too.
    /// What it costs is the guarantee: commit the facts first and every reader in the fleet gets a window,
    /// once per batch per server per cycle, in which query_stats rows carry digests that resolve to
    /// nothing — NULL text and NULL plan, no error.</para>
    /// </summary>
    [Fact]
    public void WriteBatchAsync_FlushesTheDimensionsInsideTheTransaction_BeforeItCommits()
    {
        var root = FindRepoRoot();
        Assert.True(root is not null, RepoRootNotFound);

        var path = Path.Combine(
            root!, "Darling", "PerformanceMonitor.Darling.Service", "DarlingCollectorRunner.cs");
        Assert.True(File.Exists(path), $"Expected the collector runner at '{path}'.");

        var source = File.ReadAllText(path);

        /* Exactly one of each, so comparing positions is unambiguous. If a second COPY or a second commit
           ever appears in this file, this pin must be rescoped to the method body rather than quietly
           comparing the wrong pair. */
        foreach (var call in new[]
        {
            "importer.CompleteAsync(",
            "PayloadDimensionWriter.FlushAsync(",
            "transaction.CommitAsync(",
        })
        {
            Assert.Equal(1, OccurrencesOf(source, call));
        }

        var completed = source.IndexOf("importer.CompleteAsync(", StringComparison.Ordinal);
        var flushed = source.IndexOf("PayloadDimensionWriter.FlushAsync(", StringComparison.Ordinal);
        var committed = source.IndexOf("transaction.CommitAsync(", StringComparison.Ordinal);

        Assert.True(
            completed < flushed && flushed < committed,
            "DarlingCollectorRunner.WriteBatchAsync must complete the COPY, then flush the payload " +
            "dimensions, then commit — all in ONE transaction. Committing before the dimension flush " +
            "publishes fact rows whose digests resolve to nothing, and every reader that goes through " +
            "v_query_stats in that window gets NULL text and NULL plan with no error raised anywhere.");
    }

    // ── the scanner the source guards run on ──

    private const string RepoRootNotFound =
        "Could not locate the repository root (walked up from the test binary looking for " +
        "PerformanceMonitor.sln). These guards scan the source tree, so they cannot run without it — fix " +
        "the walk-up rather than skipping, or the rules stop being enforced without anyone noticing.";

    /// <summary>The fact tables whose payload columns moved into dimensions.</summary>
    private static readonly string[] FactTables = { "query_stats", "procedure_stats" };

    /// <summary>The payload columns that are NULL on every row written since #1767.</summary>
    private static readonly string[] PayloadColumns = { "query_text", "query_plan_xml" };

    /// <summary>The view that resolves them (there is no procedure_stats twin).</summary>
    private const string ResolvingView = "v_query_stats";

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static int OccurrencesOf(string text, string value)
    {
        var count = 0;
        for (var at = text.IndexOf(value, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>Whether <paramref name="text"/> contains <paramref name="word"/> on both-side word
    /// boundaries — so <c>query_text_digest</c> does not count as a reference to <c>query_text</c>.</summary>
    private static bool ContainsWord(string text, string word)
    {
        var searchFrom = 0;
        while (searchFrom <= text.Length - word.Length)
        {
            var at = text.IndexOf(word, searchFrom, StringComparison.Ordinal);
            if (at < 0)
            {
                return false;
            }

            var after = at + word.Length;
            searchFrom = after;

            if ((at == 0 || !IsWordChar(text[at - 1])) && (after >= text.Length || !IsWordChar(text[after])))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The first <c>FROM</c>/<c>JOIN</c> reference to <paramref name="table"/>, or null.
    ///
    /// <para>Word-bounded on BOTH sides: the left boundary is what stops <c>v_query_stats</c> matching
    /// <c>query_stats</c>, the right is what stops <c>query_stats_hourly</c> (the continuous aggregate)
    /// matching it. It steps back over an optional <c>schema.</c> qualifier first, because
    /// <c>collect.query_stats</c> is the same physical table — and requiring FROM/JOIN is what keeps
    /// <c>INSERT INTO query_stats</c>, <c>ALTER TABLE collect.query_stats</c> and
    /// <c>add_retention_policy('collect.query_stats')</c> out of the rule, none of which read anything.</para>
    /// </summary>
    private static (int Index, string Text)? FromOrJoinOf(string sql, string table)
    {
        var searchFrom = 0;
        while (searchFrom <= sql.Length - table.Length)
        {
            var at = sql.IndexOf(table, searchFrom, StringComparison.Ordinal);
            if (at < 0)
            {
                return null;
            }

            var after = at + table.Length;
            searchFrom = after;

            if ((at > 0 && IsWordChar(sql[at - 1])) || (after < sql.Length && IsWordChar(sql[after])))
            {
                continue;
            }

            var cursor = at;
            if (cursor > 0 && sql[cursor - 1] == '.')
            {
                cursor--;
                while (cursor > 0 && IsWordChar(sql[cursor - 1]))
                {
                    cursor--;
                }
            }

            while (cursor > 0 && char.IsWhiteSpace(sql[cursor - 1]))
            {
                cursor--;
            }

            var keywordEnd = cursor;
            while (cursor > 0 && IsWordChar(sql[cursor - 1]))
            {
                cursor--;
            }

            var keyword = sql[cursor..keywordEnd];
            if (keyword.Equals("FROM", StringComparison.OrdinalIgnoreCase)
                || keyword.Equals("JOIN", StringComparison.OrdinalIgnoreCase))
            {
                return (cursor, sql[cursor..after]);
            }
        }

        return null;
    }

    /// <summary>
    /// Whether some <c>COALESCE(...)</c> in the literal actually wraps a payload column. Deliberately not
    /// a bare "mentions COALESCE": COALESCE is an everyday SQL function, and one used for something
    /// unrelated (a null-safe join key, say) would exempt a statement that never resolved a payload at
    /// all. Scans balanced parentheses so a nested call is read whole.
    /// </summary>
    private static bool CoalescesAPayloadColumn(string sql)
    {
        const string Coalesce = "COALESCE";
        var searchFrom = 0;
        while (searchFrom <= sql.Length - Coalesce.Length)
        {
            var at = sql.IndexOf(Coalesce, searchFrom, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                return false;
            }

            searchFrom = at + Coalesce.Length;

            var open = searchFrom;
            while (open < sql.Length && char.IsWhiteSpace(sql[open]))
            {
                open++;
            }

            if (open >= sql.Length || sql[open] != '(')
            {
                continue;
            }

            var depth = 0;
            var close = open;
            for (; close < sql.Length; close++)
            {
                if (sql[close] == '(')
                {
                    depth++;
                }
                else if (sql[close] == ')' && --depth == 0)
                {
                    break;
                }
            }

            var arguments = sql[(open + 1)..Math.Min(close, sql.Length)];
            if (PayloadColumns.Any(column => ContainsWord(arguments, column)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Every string literal in a C# source file, with its start offset.
    ///
    /// <para>Handles RAW string literals (<c>"""…"""</c>, closed by the first run of at least as many
    /// quotes) as well as verbatim (<c>@"…"</c>, where <c>""</c> is one escaped quote) and ordinary ones,
    /// because this repo's SQL is written as raw literals — a verbatim-only scanner would find almost none
    /// of it and the guard above would be silently vacuous. Comments and character literals are skipped
    /// WHOLE rather than parsed, because a stray quote inside one would otherwise open a phantom literal
    /// and desynchronise everything after it. Deliberately a small scanner rather than a regex: literal
    /// nesting and the raw-literal closing rule are not a regular language.</para>
    /// </summary>
    private static List<(string Literal, int Index)> StringLiterals(string source)
    {
        var literals = new List<(string, int)>();
        var i = 0;

        while (i < source.Length)
        {
            var c = source[i];

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                var end = source.IndexOf('\n', i);
                i = end < 0 ? source.Length : end + 1;
                continue;
            }

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                var end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? source.Length : end + 2;
                continue;
            }

            if (c == '\'')
            {
                i++;
                while (i < source.Length && source[i] != '\'')
                {
                    i += source[i] == '\\' ? 2 : 1;
                }

                i++;
                continue;
            }

            if (c != '"' && c != '@' && c != '$')
            {
                i++;
                continue;
            }

            /* $, @, $@, @$, $$ … are all legal prefixes; what decides is whether a quote follows. */
            var prefixStart = i;
            while (i < source.Length && (source[i] == '$' || source[i] == '@'))
            {
                i++;
            }

            if (i >= source.Length || source[i] != '"')
            {
                i = prefixStart + 1;
                continue;
            }

            var verbatim = source.AsSpan(prefixStart, i - prefixStart).Contains('@');
            var quoteRun = 0;
            while (i + quoteRun < source.Length && source[i + quoteRun] == '"')
            {
                quoteRun++;
            }

            /* A raw literal cannot carry the @ prefix, and @"""" is a verbatim string holding one quote —
               not a three-quote opener. Checking `verbatim` first is what keeps those apart. */
            if (!verbatim && quoteRun >= 3)
            {
                var contentStart = i + quoteRun;
                var j = contentStart;
                i = source.Length;

                while (j < source.Length)
                {
                    if (source[j] != '"')
                    {
                        j++;
                        continue;
                    }

                    var run = 0;
                    while (j + run < source.Length && source[j + run] == '"')
                    {
                        run++;
                    }

                    if (run >= quoteRun)
                    {
                        literals.Add((source[contentStart..j], contentStart));
                        i = j + run;
                        break;
                    }

                    j += run;
                }

                continue;
            }

            if (quoteRun >= 2 && !verbatim)
            {
                literals.Add((string.Empty, i + 1));
                i += 2;
                continue;
            }

            var start = i + 1;

            if (verbatim)
            {
                var builder = new StringBuilder();
                var j = start;
                while (j < source.Length)
                {
                    if (source[j] == '"')
                    {
                        if (j + 1 < source.Length && source[j + 1] == '"')
                        {
                            builder.Append('"');
                            j += 2;
                            continue;
                        }

                        break;
                    }

                    builder.Append(source[j]);
                    j++;
                }

                literals.Add((builder.ToString(), start));
                i = j + 1;
                continue;
            }

            var k = start;
            while (k < source.Length && source[k] != '"' && source[k] != '\n')
            {
                k += source[k] == '\\' ? 2 : 1;
            }

            literals.Add((source[start..Math.Min(k, source.Length)], start));
            i = k + 1;
        }

        return literals;
    }

    /// <summary>
    /// Walks up from the test output directory to the repo root — the directory holding
    /// <c>PerformanceMonitor.sln</c>. Same walk-up idiom as <c>DocCommentHygieneTests.FindRepoRoot</c>.
    /// </summary>
    private static string? FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && directory is not null; i++)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PerformanceMonitor.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
