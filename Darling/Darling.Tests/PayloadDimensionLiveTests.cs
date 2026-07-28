/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using NpgsqlTypes;
using PerformanceMonitor.Collectors;
using PerformanceMonitor.Darling.Service;
using PerformanceMonitor.Darling.Storage;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// The store-side half of #1767, against a real PostgreSQL (gated on DARLING_TEST_PG). Plain PostgreSQL is
/// enough — the dimension tables are deliberately NOT hypertables, so nothing here needs TimescaleDB.
///
/// <para>These are the assertions the string pins in <c>PayloadDimensionTests</c> structurally cannot make.
/// The whole change hinges on a claim about what the ENGINE does with the generated artifacts: that the
/// migration's ADD COLUMNs land, that a binary COPY built from the diversion plan writes a 32-byte digest
/// into a bytea column at the payload's ordinal and NULL into the inline one, that the upsert's ON CONFLICT
/// really collapses a repeated plan to one row, and that <c>v_query_stats</c> hands the original strings
/// back through the COALESCE. Every one of those fails SILENTLY if it is wrong — a mis-ordinal COPY
/// succeeds, a non-resolving read returns NULL, a broken dedup just stores more — which is precisely why
/// they are worth the cost of a live rig.</para>
///
/// <para>Each test mints its own server_id/server_name and its own payload content (a per-run GUID), so a
/// shared rig, a repeat run and a parallel run cannot collide, and cleanup is scoped to exactly what the
/// test wrote.</para>
/// </summary>
/* Live-fixture tests share one Postgres store; the collection serializes them so
   cross-test row churn (inserts/purges/deletes) cannot race another class's assertions. */
[Collection("live-postgres")]
public sealed class PayloadDimensionLiveTests
{
    private const string SkipReason =
        "Set DARLING_TEST_PG to a Postgres connection string to run the payload-dimension store tests.";

    /// <summary>A server identity no real store can produce (real ids are storage-name hashes).</summary>
    private static (int ServerId, string ServerName) NewServer()
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        return (-Random.Shared.Next(1_000_000, int.MaxValue), "pm1767-" + suffix);
    }

    private static async Task<NpgsqlConnection> OpenMigratedStoreAsync(string connectionString, CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await PgMigrations.MigrateAsync(connection, cancellationToken);
        return connection;
    }

    /// <summary>
    /// Writes one query_stats batch exactly the way <c>DarlingCollectorRunner.WriteBatchAsync</c> does:
    /// diversion plan from the same schema the COPY command is built from, BeginPayload/EndPayload around
    /// the definition's positional write, and the dimension flush inside the SAME transaction as the COPY —
    /// the ordering guarantee the design needs is not "dims first" but "no session ever observes a fact row
    /// whose digest has no dim row".
    ///
    /// <para>Hands back the still-OPEN transaction, because that guarantee is only observable from ANOTHER
    /// session while this one is mid-batch — see
    /// <see cref="AtomicVisibility_NoOtherSessionEverSeesAFactRowWhoseDigestHasNoDimRow"/>. The caller owns
    /// it: commit it, or dispose it to roll the batch back.</para>
    /// </summary>
    private static async Task<NpgsqlTransaction> WriteQueryStatsBatchUncommittedAsync(
        NpgsqlConnection connection,
        int serverId,
        string serverName,
        DateTime collectionTime,
        IReadOnlyList<QueryStatsCollector.Row> rows,
        CancellationToken cancellationToken)
    {
        var definition = QueryStatsCollector.Instance;
        var context = new CollectorContext
        {
            ServerId = serverId,
            ServerName = serverName,
            CollectionTime = collectionTime,
            Deltas = new CollectorDeltaCalculator(),
        };

        var writer = new PgCollectorRowWriter();
        var diversionPlan = PayloadDimensions.DiversionPlanFor(definition);
        var dimensions = new PayloadDimensionBatch();
        writer.UseDimensions(diversionPlan, dimensions);

        /* Naive-UTC storage — Npgsql rejects Kind=Utc against `timestamp`. */
        var stored = DateTime.SpecifyKind(collectionTime, DateTimeKind.Unspecified);

        var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            using (var importer = await connection.BeginBinaryImportAsync(
                PgCollectorRowWriter.CopyCommandFor(definition), cancellationToken))
            {
                writer.Importer = importer;

                foreach (var row in rows)
                {
                    await importer.StartRowAsync(cancellationToken);
                    writer.Value(CollectionIdGenerator.Next());
                    writer.Value(stored)
                          .Value(serverId)
                          .Value(serverName);

                    writer.BeginPayload();
                    definition.WritePayload(row, writer, context);
                    writer.EndPayload(definition.PayloadColumns.Count);
                }

                await importer.CompleteAsync(cancellationToken);
            }

            await PayloadDimensionWriter.FlushAsync(connection, transaction, dimensions, stored, cancellationToken);
            return transaction;
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

    /// <summary>The committing wrapper — what every test that just needs the batch landed uses.</summary>
    private static async Task WriteQueryStatsBatchAsync(
        NpgsqlConnection connection,
        int serverId,
        string serverName,
        DateTime collectionTime,
        IReadOnlyList<QueryStatsCollector.Row> rows,
        CancellationToken cancellationToken)
    {
        await using var transaction = await WriteQueryStatsBatchUncommittedAsync(
            connection, serverId, serverName, collectionTime, rows, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static QueryStatsCollector.Row NewRow(string queryHash, string? queryText, string? planXml)
        => new()
        {
            DatabaseName = "pm1767",
            QueryHash = queryHash,
            QueryPlanHash = "0xPLANHASH",
            SqlHandle = "0x" + queryHash,
            PlanHandle = "0x" + queryHash,
            QueryText = queryText,
            QueryPlanXml = planXml,
        };

    private static async Task DeleteServerRowsAsync(NpgsqlConnection connection, int serverId, CancellationToken cancellationToken)
    {
        using var cleanup = new NpgsqlCommand("DELETE FROM query_stats WHERE server_id = $1", connection);
        cleanup.Parameters.AddWithValue(serverId);
        await cleanup.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteDimRowAsync(
        NpgsqlConnection connection, string dimTable, byte[] digest, CancellationToken cancellationToken)
    {
        using var cleanup = new NpgsqlCommand($"DELETE FROM {dimTable} WHERE digest = $1", connection);
        cleanup.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = digest });
        await cleanup.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<object?> ScalarAsync(
        NpgsqlConnection connection, string sql, CancellationToken cancellationToken, params object[] parameters)
    {
        using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters)
        {
            if (parameter is byte[] bytes)
            {
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = bytes });
            }
            else
            {
                command.Parameters.AddWithValue(parameter);
            }
        }

        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value == DBNull.Value ? null : value;
    }

    /// <summary>
    /// A bytea scalar, typed. Deliberately not left as <c>object</c>: xUnit's Assert.Equal picks its
    /// element-wise sequence overload from the STATIC type, so comparing two boxed <c>byte[]</c> as
    /// <c>object</c> would fall back to reference equality and pass or fail for the wrong reason.
    /// </summary>
    private static async Task<byte[]?> ScalarBytesAsync(
        NpgsqlConnection connection, string sql, CancellationToken cancellationToken, params object[] parameters)
        => (byte[]?)await ScalarAsync(connection, sql, cancellationToken, parameters);

    // ── (a) the migration ──

    [Fact]
    public async Task Migration_CreatesBothDimensions_AddsEveryDigestColumn_AndIsIdempotent()
    {
        var connectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(connectionString), SkipReason);

        var ct = TestContext.Current.CancellationToken;
        await using var connection = await OpenMigratedStoreAsync(connectionString!, ct);

        /* A fully-migrated store is a no-op on the next run — V38 included. */
        Assert.Equal(0, await PgMigrations.MigrateAsync(connection, ct));

        foreach (var dimTable in PayloadDimensions.DimTables)
        {
            var payloadColumn = PayloadDimensions.PayloadColumnOf(dimTable);

            foreach (var (column, type) in new[]
            {
                (PayloadDimensions.DigestColumn, "bytea"),
                (payloadColumn, "text"),
                (PayloadDimensions.LastSeenColumn, "timestamp without time zone"),
            })
            {
                var actualType = await ScalarAsync(
                    connection,
                    "SELECT data_type FROM information_schema.columns WHERE table_name = $1 AND column_name = $2",
                    ct, dimTable, column);
                Assert.Equal(type, actualType);

                var nullable = await ScalarAsync(
                    connection,
                    "SELECT is_nullable FROM information_schema.columns WHERE table_name = $1 AND column_name = $2",
                    ct, dimTable, column);
                Assert.Equal("NO", nullable);
            }

            /* The digest is the PRIMARY KEY — that is what makes ON CONFLICT (digest) the dedup mechanism
               rather than a hint. */
            var primaryKeyColumn = await ScalarAsync(
                connection,
                """
                SELECT a.attname
                FROM pg_index AS i
                JOIN pg_attribute AS a ON a.attrelid = i.indrelid AND a.attnum = ANY(i.indkey)
                WHERE i.indrelid = $1::regclass
                AND   i.indisprimary
                """,
                ct, dimTable);
            Assert.Equal(PayloadDimensions.DigestColumn, primaryKeyColumn);
        }

        /* All three fact-side digest columns, nullable so the migration rewrote nothing. */
        foreach (var dimension in PayloadDimensions.All)
        {
            var type = await ScalarAsync(
                connection,
                "SELECT data_type FROM information_schema.columns WHERE table_name = $1 AND column_name = $2",
                ct, dimension.TargetTable, dimension.DigestColumn);
            Assert.Equal("bytea", type);

            var nullable = await ScalarAsync(
                connection,
                "SELECT is_nullable FROM information_schema.columns WHERE table_name = $1 AND column_name = $2",
                ct, dimension.TargetTable, dimension.DigestColumn);
            Assert.Equal("YES", nullable);
        }
    }

    // ── (b) the write path ──

    [Fact]
    public async Task WritePath_StoresDigestsOnTheFactRow_ContentInTheDimensions_AndResolvesThroughTheView()
    {
        var connectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(connectionString), SkipReason);

        var ct = TestContext.Current.CancellationToken;
        var (serverId, serverName) = NewServer();
        var queryText = $"SELECT 'pm1767-{serverName}' AS marker;";
        var planXml = $"<ShowPlanXML server=\"{serverName}\"><StmtSimple/></ShowPlanXML>";
        var textDigest = PayloadDimensions.Digest(queryText);
        var planDigest = PayloadDimensions.Digest(planXml);

        await using var connection = await OpenMigratedStoreAsync(connectionString!, ct);
        try
        {
            await WriteQueryStatsBatchAsync(
                connection, serverId, serverName, DateTime.UtcNow,
                new[] { NewRow("0xAAAA", queryText, planXml) }, ct);

            /* The fact row carries the KEYS only — the inline columns this change exists to empty are NULL. */
            using (var read = new NpgsqlCommand(
                "SELECT query_text, query_plan_xml, query_text_digest, query_plan_digest FROM query_stats WHERE server_id = $1",
                connection))
            {
                read.Parameters.AddWithValue(serverId);
                await using var reader = await read.ExecuteReaderAsync(ct);
                Assert.True(await reader.ReadAsync(ct));
                Assert.True(reader.IsDBNull(0), "query_text must be NULL on a row written since #1767");
                Assert.True(reader.IsDBNull(1), "query_plan_xml must be NULL on a row written since #1767");
                Assert.Equal(textDigest, reader.GetFieldValue<byte[]>(2));
                Assert.Equal(planDigest, reader.GetFieldValue<byte[]>(3));
                Assert.False(await reader.ReadAsync(ct));
            }

            /* The content is in the dimensions, once. */
            Assert.Equal(queryText, await ScalarAsync(
                connection, "SELECT query_text FROM query_text_dim WHERE digest = $1", ct, textDigest));
            Assert.Equal(planXml, await ScalarAsync(
                connection, "SELECT query_plan_xml FROM query_plan_dim WHERE digest = $1", ct, planDigest));

            /* And the view hands back exactly what was collected — the property every reader depends on. */
            using (var read = new NpgsqlCommand(
                "SELECT query_text, query_plan_xml FROM v_query_stats WHERE server_id = $1", connection))
            {
                read.Parameters.AddWithValue(serverId);
                await using var reader = await read.ExecuteReaderAsync(ct);
                Assert.True(await reader.ReadAsync(ct));
                Assert.Equal(queryText, reader.GetString(0));
                Assert.Equal(planXml, reader.GetString(1));
            }
        }
        finally
        {
            await DeleteServerRowsAsync(connection, serverId, ct);
            await DeleteDimRowAsync(connection, PayloadDimensions.QueryTextDimTable, textDigest, ct);
            await DeleteDimRowAsync(connection, PayloadDimensions.QueryPlanDimTable, planDigest, ct);
        }
    }

    // ── (c) the dedup that is the whole point ──

    [Fact]
    public async Task Dedup_ManyRowsSharingOnePlan_StoreOneDimRow_AndASecondBatchAddsNone()
    {
        var connectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(connectionString), SkipReason);

        var ct = TestContext.Current.CancellationToken;
        var (serverId, serverName) = NewServer();
        var planXml = $"<ShowPlanXML server=\"{serverName}\"><StmtSimple/></ShowPlanXML>";
        var planDigest = PayloadDimensions.Digest(planXml);
        var textDigests = new List<byte[]>();

        var rows = new List<QueryStatsCollector.Row>();
        for (var i = 0; i < 5; i++)
        {
            var text = $"SELECT {i} FROM pm1767_{serverName};";
            textDigests.Add(PayloadDimensions.Digest(text));
            rows.Add(NewRow($"0xHASH{i}", text, planXml));
        }

        await using var connection = await OpenMigratedStoreAsync(connectionString!, ct);
        try
        {
            await WriteQueryStatsBatchAsync(connection, serverId, serverName, DateTime.UtcNow, rows, ct);

            /* Five fact rows, one plan row. The batch collapses the repeat sightings BEFORE the upsert
               sees them, which is also what keeps Postgres from raising 21000 on the duplicate key. */
            Assert.Equal(5L, await ScalarAsync(
                connection, "SELECT COUNT(*) FROM query_stats WHERE server_id = $1", ct, serverId));
            Assert.Equal(1L, await ScalarAsync(
                connection, "SELECT COUNT(*) FROM query_plan_dim WHERE digest = $1", ct, planDigest));

            /* A SECOND collection of the same cached plan — the every-cycle repetition that made the inline
               payload 94% of the store — adds nothing. This is the ON CONFLICT path, across batches. */
            await WriteQueryStatsBatchAsync(connection, serverId, serverName, DateTime.UtcNow, rows, ct);

            Assert.Equal(10L, await ScalarAsync(
                connection, "SELECT COUNT(*) FROM query_stats WHERE server_id = $1", ct, serverId));
            Assert.Equal(1L, await ScalarAsync(
                connection, "SELECT COUNT(*) FROM query_plan_dim WHERE digest = $1", ct, planDigest));
        }
        finally
        {
            await DeleteServerRowsAsync(connection, serverId, ct);
            await DeleteDimRowAsync(connection, PayloadDimensions.QueryPlanDimTable, planDigest, ct);
            foreach (var digest in textDigests)
            {
                await DeleteDimRowAsync(connection, PayloadDimensions.QueryTextDimTable, digest, ct);
            }
        }
    }

    // ── (d) the transition ──

    [Fact]
    public async Task View_StillReturnsInlinePayloads_ForRowsWrittenBeforeTheDimensionsExisted()
    {
        var connectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(connectionString), SkipReason);

        var ct = TestContext.Current.CancellationToken;
        var (serverId, serverName) = NewServer();
        var queryText = $"SELECT 'legacy-{serverName}';";
        var planXml = $"<ShowPlanXML legacy=\"{serverName}\"/>";

        await using var connection = await OpenMigratedStoreAsync(connectionString!, ct);
        try
        {
            /* The pre-#1767 shape: payload inline, digests NULL. Migration is ZERO-REWRITE, so every row in
               a live store looks like this until raw retention ages it out — roughly the first four days
               after deploy, during which the COALESCE arm is the ONLY thing keeping the product populated. */
            using (var insert = new NpgsqlCommand(
                """
                INSERT INTO query_stats
                    (collection_id, collection_time, server_id, server_name, database_name, query_hash, query_text, query_plan_xml)
                VALUES ($1, $2, $3, $4, 'pm1767', '0xLEGACY', $5, $6)
                """,
                connection))
            {
                insert.Parameters.AddWithValue(CollectionIdGenerator.Next());
                insert.Parameters.AddWithValue(DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified));
                insert.Parameters.AddWithValue(serverId);
                insert.Parameters.AddWithValue(serverName);
                insert.Parameters.AddWithValue(queryText);
                insert.Parameters.AddWithValue(planXml);
                await insert.ExecuteNonQueryAsync(ct);
            }

            using (var read = new NpgsqlCommand(
                "SELECT query_text, query_plan_xml, query_text_digest, query_plan_digest FROM v_query_stats WHERE server_id = $1",
                connection))
            {
                read.Parameters.AddWithValue(serverId);
                await using var reader = await read.ExecuteReaderAsync(ct);
                Assert.True(await reader.ReadAsync(ct));
                Assert.Equal(queryText, reader.GetString(0));
                Assert.Equal(planXml, reader.GetString(1));
                Assert.True(reader.IsDBNull(2), "a pre-#1767 row carries no text digest");
                Assert.True(reader.IsDBNull(3), "a pre-#1767 row carries no plan digest");
            }
        }
        finally
        {
            await DeleteServerRowsAsync(connection, serverId, ct);
        }
    }

    // ── (e) the last_seen watermark and its churn guard ──

    [Fact]
    public async Task LastSeen_RefreshesOnlyOncePerHour_SoAHotDimTakesOneUpdateNotOnePerCycle()
    {
        var connectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(connectionString), SkipReason);

        var ct = TestContext.Current.CancellationToken;
        var payload = $"<ShowPlanXML watermark=\"{Guid.NewGuid():N}\"/>";
        var digest = PayloadDimensions.Digest(payload);
        var t0 = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Unspecified);

        await using var connection = await OpenMigratedStoreAsync(connectionString!, ct);
        try
        {
            async Task FlushAtAsync(DateTime collectionTime)
            {
                var batch = new PayloadDimensionBatch();
                batch.Add(PayloadDimensions.QueryPlanDimTable, digest, payload);

                await using var transaction = await connection.BeginTransactionAsync(ct);
                await PayloadDimensionWriter.FlushAsync(connection, transaction, batch, collectionTime, ct);
                await transaction.CommitAsync(ct);
            }

            async Task<DateTime> LastSeenAsync()
                => (DateTime)(await ScalarAsync(
                    connection, "SELECT last_seen FROM query_plan_dim WHERE digest = $1", ct, digest))!;

            await FlushAtAsync(t0);
            Assert.Equal(t0, await LastSeenAsync());

            /* Within the hour: NOT refreshed. Every referenced dim row would otherwise take an UPDATE every
               collection cycle — a dead tuple per row per minute on a hot table — for a watermark whose
               only consumer has a multi-day horizon. */
            await FlushAtAsync(t0.AddMinutes(30));
            Assert.Equal(t0, await LastSeenAsync());

            /* Past the hour: refreshed. The guard caps the churn at 60x less, and stays correct as long as
               the GC margin exceeds an hour (it is a full day). */
            await FlushAtAsync(t0.AddHours(2));
            Assert.Equal(t0.AddHours(2), await LastSeenAsync());

            /* And a REPLAYED/backfilled batch cannot stamp content as fresher than it is: the watermark
               never moves backwards, because the guard's comparison is one-directional. */
            await FlushAtAsync(t0.AddMinutes(5));
            Assert.Equal(t0.AddHours(2), await LastSeenAsync());
        }
        finally
        {
            await DeleteDimRowAsync(connection, PayloadDimensions.QueryPlanDimTable, digest, ct);
        }
    }

    // ── (f) the GC ──

    [Fact]
    public async Task Gc_DeletesDimRowsPastTheHorizon_AndKeepsTheOnesInsideIt()
    {
        var connectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(connectionString), SkipReason);

        var ct = TestContext.Current.CancellationToken;
        var run = Guid.NewGuid().ToString("N")[..12];
        var expiredPayload = $"<ShowPlanXML gc=\"expired-{run}\"/>";
        var livePayload = $"<ShowPlanXML gc=\"live-{run}\"/>";
        var expiredDigest = PayloadDimensions.Digest(expiredPayload);
        var liveDigest = PayloadDimensions.Digest(livePayload);

        /* Deliberately ancient timestamps around an ancient cutoff, so the sweep can only ever touch rows
           this test planted — the GC is fleet-wide (a dim row has no server_id) and this class shares a rig. */
        var expiredSeen = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var liveSeen = new DateTime(2000, 6, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var cutoff = new DateTime(2000, 3, 1, 0, 0, 0, DateTimeKind.Unspecified);

        await using var connection = await OpenMigratedStoreAsync(connectionString!, ct);
        try
        {
            foreach (var (digest, payload, lastSeen) in new[]
            {
                (expiredDigest, expiredPayload, expiredSeen),
                (liveDigest, livePayload, liveSeen),
            })
            {
                using var insert = new NpgsqlCommand(
                    "INSERT INTO query_plan_dim (digest, query_plan_xml, last_seen) VALUES ($1, $2, $3)",
                    connection);
                insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = digest });
                insert.Parameters.AddWithValue(payload);
                insert.Parameters.AddWithValue(lastSeen);
                await insert.ExecuteNonQueryAsync(ct);
            }

            using (var gc = new NpgsqlCommand(
                DarlingRetention.TimeSlicedDeleteSql(
                    PayloadDimensions.QueryPlanDimTable, PayloadDimensions.LastSeenColumn),
                connection))
            {
                gc.Parameters.AddWithValue(cutoff);

                /* Drained the way production drains it: the statement clears ONE time slice per
                   execution, so a single call would only reach this test's row when it happens to sit
                   in the oldest slice on the rig. DrainBatchesAsync is the same loop DarlingRetention
                   runs, so this exercises the real termination condition rather than a test-only one. */
                var swept = await DarlingRetention.DrainBatchesAsync(
                    token => gc.ExecuteNonQueryAsync(token), batchSize: 1, ct);

                /* At least this test's expired row. The GC is fleet-wide, so an exact count would be a
                   flake waiting on a rig that happens to hold another ancient row; the two scoped counts
                   below are the actual pin. */
                Assert.True(swept >= 1, "the expired dim row must be swept");
            }

            Assert.Equal(0L, await ScalarAsync(
                connection, "SELECT COUNT(*) FROM query_plan_dim WHERE digest = $1", ct, expiredDigest));
            Assert.Equal(1L, await ScalarAsync(
                connection, "SELECT COUNT(*) FROM query_plan_dim WHERE digest = $1", ct, liveDigest));
        }
        finally
        {
            await DeleteDimRowAsync(connection, PayloadDimensions.QueryPlanDimTable, expiredDigest, ct);
            await DeleteDimRowAsync(connection, PayloadDimensions.QueryPlanDimTable, liveDigest, ct);
        }
    }

    // ── (g) NUL handling ──

    [Fact]
    public async Task NulLadenPayload_RoundTripsStripped_AndItsDigestKeysTheStoredBytes()
    {
        var connectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(connectionString), SkipReason);

        var ct = TestContext.Current.CancellationToken;
        var (serverId, serverName) = NewServer();

        /* SQL Server NVARCHAR allows embedded NUL; Postgres text rejects the raw byte with 22021 and fails
           the whole COPY batch (#1614). The strip has to happen BEFORE the hash, or the digest keys bytes
           that were never stored and the dim lookup silently misses on every re-collection. */
        var raw = $"SELECT '\0nul-{serverName}\0' AS marker;";
        var stripped = PgCollectorRowWriter.StripEmbeddedNuls(raw);
        Assert.DoesNotContain('\0', stripped);
        var strippedDigest = PayloadDimensions.Digest(stripped);
        Assert.NotEqual(strippedDigest, PayloadDimensions.Digest(raw));

        await using var connection = await OpenMigratedStoreAsync(connectionString!, ct);
        try
        {
            await WriteQueryStatsBatchAsync(
                connection, serverId, serverName, DateTime.UtcNow,
                new[] { NewRow("0xNUL", raw, null) }, ct);

            /* The digest on the fact row is the digest of what the dim actually holds. */
            Assert.Equal(strippedDigest, await ScalarBytesAsync(
                connection, "SELECT query_text_digest FROM query_stats WHERE server_id = $1", ct, serverId));
            Assert.Equal(stripped, await ScalarAsync(
                connection, "SELECT query_text FROM query_text_dim WHERE digest = $1", ct, strippedDigest));
            Assert.Equal(stripped, await ScalarAsync(
                connection, "SELECT query_text FROM v_query_stats WHERE server_id = $1", ct, serverId));

            /* A NULL payload stays NULL — nothing to dedupe and no dim row to point at. */
            Assert.Null(await ScalarAsync(
                connection, "SELECT query_plan_digest FROM query_stats WHERE server_id = $1", ct, serverId));
        }
        finally
        {
            await DeleteServerRowsAsync(connection, serverId, ct);
            await DeleteDimRowAsync(connection, PayloadDimensions.QueryTextDimTable, strippedDigest, ct);
        }
    }

    // ── (h) the atomicity the whole ordering argument rests on ──

    /// <summary>
    /// The ordering guarantee, proved from the only vantage point that can see it: a SECOND session.
    ///
    /// <para>The guarantee is NOT "dims are inserted before facts". It cannot be — the digests are only
    /// known once the COPY has streamed the payloads, and a dims-first pass would mean running the
    /// definition's <c>WritePayload</c> twice, which is unsafe because <c>WritePayload</c> CONSUMES the
    /// delta state (<c>context.Deltas.CalculateDelta</c>) and a second pass would report every delta as
    /// zero. The guarantee is the STRONGER one a shared transaction gives for free: <b>no other session
    /// ever observes a fact row whose digest has no dimension row</b>.</para>
    ///
    /// <para>So this test opens the window a non-transactional implementation would leak through.
    /// Connection A runs the whole batch — COPY, importer completion, dimension flush — and stops short of
    /// COMMIT. Connection B, a separate connection at the default READ COMMITTED (what the viewer, the MCP
    /// readers and the analysis engine all are), must see NOTHING: not the facts, not the dims. An
    /// implementation that let the importer's own completion be the commit and upserted the dimensions
    /// afterwards would, at this exact instant, be showing every reader fact rows whose digests resolve to
    /// nothing — and <c>v_query_stats</c> would hand those readers NULL text and NULL plan with no error
    /// anywhere, which reads as a collection outage rather than as a bug. After the commit both sides
    /// appear together, and the anti-join a reader would run comes back empty.</para>
    /// </summary>
    [Fact]
    public async Task AtomicVisibility_NoOtherSessionEverSeesAFactRowWhoseDigestHasNoDimRow()
    {
        var connectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(connectionString), SkipReason);

        var ct = TestContext.Current.CancellationToken;
        var (serverId, serverName) = NewServer();
        var queryText = $"SELECT 'atomic-{serverName}' AS marker;";
        var planXml = $"<ShowPlanXML atomic=\"{serverName}\"><StmtSimple/></ShowPlanXML>";
        var textDigest = PayloadDimensions.Digest(queryText);
        var planDigest = PayloadDimensions.Digest(planXml);

        /* Two rows sharing one text and one plan: the dedup shape a real batch has, so the post-commit
           counts pin BOTH that the facts landed and that they collapsed onto single dim rows. */
        var rows = new[]
        {
            NewRow("0xATOMIC1", queryText, planXml),
            NewRow("0xATOMIC2", queryText, planXml),
        };

        /* The WRITER's session. */
        await using var writer = await OpenMigratedStoreAsync(connectionString!, ct);

        /* The OBSERVER's session. Deliberately a separate NpgsqlConnection and deliberately NOT migrated:
           only the writer touches DDL, so nothing here can serialize behind the writer's own work and
           accidentally turn "sees nothing" into "was blocked from looking".

           It still needs the search_path, and it must SET it rather than inherit one. MigrateAsync
           SETs the path on whatever session it is handed; the store-wide default comes from
           ALTER DATABASE, which reaches only sessions established AFTER it commits — and Npgsql
           POOLS sessions, so a physical connection opened before the first migration keeps the
           default "$user", public for its whole life and resolves bare `query_stats` to nothing
           (42P01). On a long-lived store every session postdates that ALTER by days, which is why
           this passes locally and failed on CI's fresh cluster, where the pool is full of
           connections older than the migration. Setting it here makes the observer independent of
           both the ALTER's timing and the pool's reuse. */
        await using var observer = new NpgsqlConnection(connectionString!);
        await observer.OpenAsync(ct);
        await using (var setPath = new NpgsqlCommand("SET search_path = " + PgSchemaGenerator.SearchPath, observer))
        {
            await setPath.ExecuteNonQueryAsync(ct);
        }

        async Task<long> ObservedFactRowsAsync()
            => (long)(await ScalarAsync(
                observer, "SELECT COUNT(*) FROM query_stats WHERE server_id = $1", ct, serverId))!;

        async Task<long> ObservedDimRowsAsync(string dimTable, byte[] digest)
            => (long)(await ScalarAsync(
                observer, $"SELECT COUNT(*) FROM {dimTable} WHERE digest = $1", ct, digest))!;

        /* The property as a reader would state it: fact rows carrying a digest that resolves to nothing.
           Zero is the only acceptable answer at EVERY instant, which is why it is asked from the
           observer's session and not the writer's — inside the writer's transaction it is trivially zero
           however the write path is ordered. */
        async Task<long> DanglingDigestsAsync(string digestColumn, string dimTable)
            => (long)(await ScalarAsync(
                observer,
                $"""
                 SELECT COUNT(*)
                 FROM query_stats AS f
                 WHERE f.server_id = $1
                 AND   f.{digestColumn} IS NOT NULL
                 AND   NOT EXISTS (SELECT 1 FROM {dimTable} AS d WHERE d.digest = f.{digestColumn})
                 """,
                ct, serverId))!;

        try
        {
            await using (var transaction = await WriteQueryStatsBatchUncommittedAsync(
                writer, serverId, serverName, DateTime.UtcNow, rows, ct))
            {
                /* THE OBSERVATION WINDOW — everything written, nothing committed. */
                Assert.Equal(0L, await ObservedFactRowsAsync());
                Assert.Equal(0L, await ObservedDimRowsAsync(PayloadDimensions.QueryTextDimTable, textDigest));
                Assert.Equal(0L, await ObservedDimRowsAsync(PayloadDimensions.QueryPlanDimTable, planDigest));

                await transaction.CommitAsync(ct);
            }

            /* One commit, both sides visible at once. */
            Assert.Equal(2L, await ObservedFactRowsAsync());
            Assert.Equal(1L, await ObservedDimRowsAsync(PayloadDimensions.QueryTextDimTable, textDigest));
            Assert.Equal(1L, await ObservedDimRowsAsync(PayloadDimensions.QueryPlanDimTable, planDigest));

            Assert.Equal(0L, await DanglingDigestsAsync("query_text_digest", PayloadDimensions.QueryTextDimTable));
            Assert.Equal(0L, await DanglingDigestsAsync("query_plan_digest", PayloadDimensions.QueryPlanDimTable));
        }
        finally
        {
            await DeleteServerRowsAsync(writer, serverId, ct);
            await DeleteDimRowAsync(writer, PayloadDimensions.QueryTextDimTable, textDigest, ct);
            await DeleteDimRowAsync(writer, PayloadDimensions.QueryPlanDimTable, planDigest, ct);
        }
    }

    /// <summary>
    /// The dimension GC must NOT prune while a table that feeds it failed to purge this cycle (#1782).
    ///
    /// <para>The cutoff assumes facts past their retention are gone. The fact loop is failure-isolated, so a
    /// table whose drop_chunks fails AND whose DELETE fallback then also fails is warned, skipped, and the
    /// sweep continues straight into the GC — pruning dimension rows those surviving facts still reference.
    /// The digests dangle and the resolving view serves NULL payload with no error raised anywhere, which is
    /// the whole reason this is a guard and not a comment.</para>
    ///
    /// <para>The failure is injected by RENAMING query_stats for the duration, which is a genuine total
    /// failure of both purge paths (the relation the statements name does not exist) rather than a simulated
    /// one — and it exercises the real seam: the sweep sets the flag, and the GC reads it. It is restored in
    /// a finally, and the whole live collection is serialized, so no sibling class can observe the rename.</para>
    /// </summary>
    [Fact]
    public async Task DimensionGc_DefersWhenADimFeedingTableFailedItsPurge_ThenPrunesOnceItSucceeds()
    {
        var connectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(connectionString), SkipReason);

        var ct = TestContext.Current.CancellationToken;
        await using var connection = await OpenMigratedStoreAsync(connectionString!, ct);

        /* Far past the ~32-day cutoff, so an unguarded GC would certainly take it. */
        var digest = PayloadDimensions.Digest("pm1782-" + Guid.NewGuid().ToString("N"));
        var ancient = DateTime.SpecifyKind(DateTime.UtcNow.AddDays(-400), DateTimeKind.Unspecified);

        await using var postgres = NpgsqlDataSource.Create(connectionString!);
        var renamed = false;

        try
        {
            using (var seed = new NpgsqlCommand(
                $"INSERT INTO {PayloadDimensions.QueryPlanDimTable} (digest, query_plan_xml, last_seen) " +
                "VALUES ($1, $2, $3) ON CONFLICT (digest) DO NOTHING", connection))
            {
                seed.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = digest });
                seed.Parameters.AddWithValue("<ShowPlanXML pm1782=\"1\"/>");
                seed.Parameters.AddWithValue(ancient);
                await seed.ExecuteNonQueryAsync(ct);
            }

            Assert.Equal(1L, await DimRowCountAsync(connection, digest, ct));

            using (var rename = new NpgsqlCommand(
                "ALTER TABLE collect.query_stats RENAME TO query_stats_pm1782", connection))
            {
                await rename.ExecuteNonQueryAsync(ct);
                renamed = true;
            }

            var deferredLog = new CapturingTestLogger();
            await DarlingRetention.PurgeAsync(postgres, timescaleAvailable: true, deferredLog, ct);

            /* The SUBSTANTIVE property first, deliberately: this is the assertion that distinguishes
               "the guard held" from "the guard logged and pruned anyway", so it is the one a mutation
               must land on. Asserting the log line first would let a broken guard go red on missing
               TEXT while the actual data loss went unexamined. */
            Assert.Equal(1L, await DimRowCountAsync(connection, digest, ct));
            Assert.Contains("dimension GC deferred", deferredLog.Joined, StringComparison.Ordinal);

            using (var restore = new NpgsqlCommand(
                "ALTER TABLE collect.query_stats_pm1782 RENAME TO query_stats", connection))
            {
                await restore.ExecuteNonQueryAsync(ct);
                renamed = false;
            }

            /* Control: with the purge healthy again the guard opens and the same row is pruned, so the test
               cannot pass merely because the GC never runs. */
            var prunedLog = new CapturingTestLogger();
            await DarlingRetention.PurgeAsync(postgres, timescaleAvailable: true, prunedLog, ct);

            Assert.DoesNotContain("dimension GC deferred", prunedLog.Joined, StringComparison.Ordinal);
            Assert.Equal(0L, await DimRowCountAsync(connection, digest, ct));
        }
        finally
        {
            if (renamed)
            {
                using var restore = new NpgsqlCommand(
                    "ALTER TABLE IF EXISTS collect.query_stats_pm1782 RENAME TO query_stats", connection);
                await restore.ExecuteNonQueryAsync(ct);
            }

            await DeleteDimRowAsync(connection, PayloadDimensions.QueryPlanDimTable, digest, ct);
        }
    }

    private static async Task<long> DimRowCountAsync(NpgsqlConnection connection, byte[] digest, CancellationToken ct)
    {
        using var read = new NpgsqlCommand(
            $"SELECT COUNT(*) FROM {PayloadDimensions.QueryPlanDimTable} WHERE digest = $1", connection);
        read.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = digest });
        return (long)(await read.ExecuteScalarAsync(ct))!;
    }
}
