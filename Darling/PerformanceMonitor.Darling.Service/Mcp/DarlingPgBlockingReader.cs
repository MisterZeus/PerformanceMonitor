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

namespace PerformanceMonitor.Darling.Service.Mcp;

/// <summary>
/// Reads blocking chains from the stored edge list (<c>pg_blocking_edges</c>), assembled into one row per
/// captured chain with its root blocker attributed.
/// <para>This is where storing edges rather than a rendered tree pays for itself: the collector wrote
/// (blocked, blocking) pairs and knew nothing about chains, and the questions that actually matter — who is
/// at the root, how deep does it go, how many sessions are behind it, has this same backend been the root
/// all afternoon — are all answered here in SQL over those pairs.</para>
/// </summary>
public static class DarlingPgBlockingReader
{
    public sealed record PgBlockingChainRow(
        DateTime CapturedAt,
        long RootBackendId,
        int RootPid,
        string? DatabaseName,
        string? RootUsername,
        string? RootApplicationName,
        string? RootState,
        string? RootQuery,
        bool RootIsIdleInTransaction,
        long RootXactDurationMs,
        long RootQueryDurationMs,
        int TotalVictims,
        int DirectVictims,
        int MaxDepth,
        long WorstVictimWaitMs,
        string? WorstVictimQuery,
        /* NULL, not 0 or 1, when the root's own backend id did not resolve (the collector's
           vanished-blocker sentinel). Recurrence is genuinely UNKNOWN there, and "seen once" is a
           different claim from "cannot tell". */
        long? SamplesAsRoot,
        bool QueryTextMayBeTruncated);

    /// <summary>
    /// One row per (capture, root blocker), with the chain behind it measured and the root's own state
    /// attached.
    ///
    /// <para><b>Roots are found by absence.</b> A backend is a root when it blocks something and is not
    /// itself blocked in the same capture. That definition is why the collector had to store the whole edge
    /// set per capture rather than only the pairs someone asked about — a root cannot be recognised from one
    /// edge in isolation.</para>
    ///
    /// <para><b>The recursion is depth-capped at 32, and that guard is not decoration.</b> A cycle in the
    /// edge set would make an uncapped recursive CTE run until it exhausted memory. Cycles are rare but
    /// genuinely possible: PostgreSQL's deadlock detector resolves them, but only after
    /// <c>deadlock_timeout</c> (1s by default), so a capture can land inside that window and record a true
    /// cycle. No real chain approaches 32, so the cap costs nothing and removes the failure mode.</para>
    ///
    /// <para><b><c>samples_as_root</c> is keyed on the synthetic backend id, not the pid</b>, which is the
    /// whole reason that column exists. It answers "has this been the same stuck backend all along, or a
    /// succession of different ones that happened to reuse a pid" — and those two have different remedies.
    /// A pid-keyed count cannot tell them apart and would silently merge them on a busy instance.</para>
    ///
    /// <para><b>Ordered worst-first, not newest-first</b> (widest chain, then deepest, then most recent).
    /// The question this read serves is "what was the worst blocking in this window", and a newest-first
    /// ordering under a row limit would answer a different one — it would return the most recent captures
    /// and could omit the incident entirely.</para>
    ///
    /// <para><b><c>WITH RECURSIVE</c>, and the keyword goes on the FIRST CTE.</b> PostgreSQL scopes
    /// <c>RECURSIVE</c> to the whole <c>WITH</c> clause, not to the one CTE that needs it, so writing
    /// <c>WITH edges AS ... chain AS (... UNION ALL ... FROM chain ...)</c> fails outright with
    /// <c>relation "chain" does not exist</c> — a forward reference it will not resolve. It is a runtime
    /// error on the first call, not a compile-time one, which is why this was found by running the text
    /// against a real instance rather than by reading it.</para>
    ///
    /// <para>$1 server_id, $2/$3 window (naive UTC), $4 row limit.</para>
    /// </summary>
    public const string PgBlockingChainsSql = """
        WITH RECURSIVE edges AS (
            SELECT
                collection_id,
                collection_time,
                blocked_pid,
                blocking_pid,
                blocking_backend_id,
                blocked_query,
                blocked_query_duration_ms,
                blocking_username,
                blocking_application_name,
                blocking_state,
                blocking_query,
                blocking_is_idle_in_transaction,
                blocking_xact_duration_ms,
                blocking_query_duration_ms,
                database_name,
                query_text_may_be_truncated
            FROM pg_blocking_edges
            WHERE server_id = $1
            AND   collection_time >= $2
            AND   collection_time <= $3
        ),
        roots AS (
            SELECT DISTINCT
                e.collection_id,
                e.collection_time,
                e.blocking_pid,
                e.blocking_backend_id
            FROM edges AS e
            WHERE NOT EXISTS (
                SELECT 1
                FROM edges AS upstream
                WHERE upstream.collection_id = e.collection_id
                AND   upstream.blocked_pid = e.blocking_pid
            )
        ),
        chain AS (
            SELECT
                r.collection_id,
                r.blocking_pid AS root_pid,
                e.blocked_pid,
                e.blocked_query,
                e.blocked_query_duration_ms,
                1 AS depth,
                ARRAY[r.blocking_pid, e.blocked_pid] AS visited
            FROM roots AS r
            JOIN edges AS e
              ON  e.collection_id = r.collection_id
              AND e.blocking_pid = r.blocking_pid

            UNION ALL

            SELECT
                c.collection_id,
                c.root_pid,
                e.blocked_pid,
                e.blocked_query,
                e.blocked_query_duration_ms,
                c.depth + 1,
                c.visited || e.blocked_pid
            FROM chain AS c
            JOIN edges AS e
              ON  e.collection_id = c.collection_id
              AND e.blocking_pid = c.blocked_pid
            WHERE c.depth < 32
            /* Never revisit a backend already on this walk. Without it a cycle hanging off an otherwise
               legitimate root is walked until the depth cap: root A blocks B while B/C/D form a cycle among
               themselves, B is correctly excluded from roots (it IS blocked) but A still qualifies, and the
               walk goes B -> C -> D -> B -> ... to 32. The cap stops the runaway but chain_stats then reports
               max_depth = 32 and a worst victim drawn from repeated revisits of the same three backends,
               which is indistinguishable from a genuine 32-deep chain. With the guard the counts are the
               DISTINCT set, and the cycle itself is reported by PgBlockingCyclesSql instead. */
            AND   e.blocked_pid <> ALL(c.visited)
        ),
        chain_stats AS (
            SELECT
                collection_id,
                root_pid,
                count(DISTINCT blocked_pid)::int AS total_victims,
                count(DISTINCT blocked_pid) FILTER (WHERE depth = 1)::int AS direct_victims,
                max(depth)::int AS max_depth,
                max(coalesce(blocked_query_duration_ms, -1)) AS worst_victim_wait_ms
            FROM chain
            GROUP BY collection_id, root_pid
        ),
        worst_victim AS (
            SELECT DISTINCT ON (collection_id, root_pid)
                collection_id,
                root_pid,
                blocked_query
            FROM chain
            ORDER BY collection_id, root_pid, coalesce(blocked_query_duration_ms, -1) DESC
        ),
        root_detail AS (
            SELECT DISTINCT ON (e.collection_id, e.blocking_pid)
                e.collection_id,
                e.collection_time,
                e.blocking_pid,
                e.blocking_backend_id,
                e.database_name,
                e.blocking_username,
                e.blocking_application_name,
                e.blocking_state,
                e.blocking_query,
                e.blocking_is_idle_in_transaction,
                e.blocking_xact_duration_ms,
                e.blocking_query_duration_ms,
                e.query_text_may_be_truncated
            FROM edges AS e
            JOIN roots AS r
              ON  r.collection_id = e.collection_id
              AND r.blocking_pid = e.blocking_pid
            ORDER BY e.collection_id, e.blocking_pid, e.blocked_pid
        ),
        recurrence AS (
            SELECT
                blocking_backend_id,
                count(DISTINCT collection_id) AS samples_as_root
            FROM roots
            /* Exclude the vanished-blocker sentinel. The collector stores
               coalesce(blocker.backend_id, 0), so every root whose own row had already left
               pg_stat_activity lands on id 0 — and grouping those together counts unrelated one-off
               incidents in different captures as repeat appearances of one backend. That is precisely the
               conflation the synthetic backend id exists to prevent, arriving through the fallback instead
               of through pid reuse. Excluded rather than counted, so the final LEFT JOIN yields NULL and
               the read reports recurrence as UNKNOWN rather than inventing a number. */
            WHERE blocking_backend_id <> 0
            GROUP BY blocking_backend_id
        )
        /* Every output column is aliased, including the ones whose name looks obvious. The C# reader is
           positional so it does not care — but an unaliased coalesce() comes back named "coalesce", and
           three of them did, so a psql session debugging this query saw three identical column headings.
           A query this intricate has to be readable in the tool people will actually reach for. */
        SELECT
            d.collection_time                        AS captured_at,
            d.blocking_backend_id                    AS root_backend_id,
            d.blocking_pid                           AS root_pid,
            d.database_name                          AS database_name,
            d.blocking_username                      AS root_username,
            d.blocking_application_name              AS root_application_name,
            d.blocking_state                         AS root_state,
            d.blocking_query                         AS root_query,
            d.blocking_is_idle_in_transaction        AS root_is_idle_in_transaction,
            coalesce(d.blocking_xact_duration_ms, -1)  AS root_xact_duration_ms,
            coalesce(d.blocking_query_duration_ms, -1) AS root_query_duration_ms,
            s.total_victims                          AS total_victims,
            s.direct_victims                         AS direct_victims,
            s.max_depth                              AS max_depth,
            s.worst_victim_wait_ms                   AS worst_victim_wait_ms,
            v.blocked_query                          AS worst_victim_query,
            c.samples_as_root                        AS samples_as_root,
            d.query_text_may_be_truncated            AS query_text_may_be_truncated
        FROM root_detail AS d
        JOIN chain_stats AS s
          ON  s.collection_id = d.collection_id
          AND s.root_pid = d.blocking_pid
        LEFT JOIN worst_victim AS v
          ON  v.collection_id = d.collection_id
          AND v.root_pid = d.blocking_pid
        LEFT JOIN recurrence AS c
          ON  c.blocking_backend_id = d.blocking_backend_id
        ORDER BY s.total_victims DESC, s.max_depth DESC, d.collection_time DESC
        LIMIT $4
        """;

    public static async Task<List<PgBlockingChainRow>> GetPgBlockingChainsAsync(
        NpgsqlDataSource postgres, int serverId, DateTime startUtc, DateTime endUtc, int limit,
        CancellationToken cancellationToken = default)
    {
        var rows = new List<PgBlockingChainRow>();
        await using var command = postgres.CreateCommand(PgBlockingChainsSql);
        command.Parameters.AddWithValue(serverId);
        /* SpecifyKind(Unspecified), not the bare value. Npgsql does not reject Kind=Utc — it infers
           timestamptz, and PostgreSQL then zone-shifts the window against the store's NAIVE timestamp
           columns, so east of UTC the window silently slides off the data. Same convention as every
           other PostgreSQL read (DarlingPgXminReader, and the alert adapter's NaiveUtcNow). */
        command.Parameters.AddWithValue(DateTime.SpecifyKind(startUtc, DateTimeKind.Unspecified));
        command.Parameters.AddWithValue(DateTime.SpecifyKind(endUtc, DateTimeKind.Unspecified));
        command.Parameters.AddWithValue(limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new PgBlockingChainRow(
                reader.GetDateTime(0),
                reader.IsDBNull(1) ? 0 : reader.GetInt64(1),
                reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                !reader.IsDBNull(8) && reader.GetBoolean(8),
                reader.IsDBNull(9) ? -1 : reader.GetInt64(9),
                reader.IsDBNull(10) ? -1 : reader.GetInt64(10),
                reader.IsDBNull(11) ? 0 : reader.GetInt32(11),
                reader.IsDBNull(12) ? 0 : reader.GetInt32(12),
                reader.IsDBNull(13) ? 0 : reader.GetInt32(13),
                reader.IsDBNull(14) ? -1 : reader.GetInt64(14),
                reader.IsDBNull(15) ? null : reader.GetString(15),
                reader.IsDBNull(16) ? null : reader.GetInt64(16),
                !reader.IsDBNull(17) && reader.GetBoolean(17)));
        }

        return rows;
    }

    public sealed record PgBlockingCycleRow(
        DateTime CapturedAt,
        int ParticipantCount,
        int[] Pids,
        string? DatabaseName,
        string? ApplicationName);

    /// <summary>
    /// Backends caught in a lock CYCLE — each one reachable from itself through the edge list.
    ///
    /// <para><b>This exists because the chain read cannot report them, and finding that out was the point of
    /// probing it.</b> <c>chains</c> identifies a root by absence: a backend that blocks something and is not
    /// itself blocked. In a cycle every participant is blocked, so there is no root, so the entire cyclic
    /// component is silently dropped — 0 rows from a capture that recorded real blocking. For a collector
    /// whose whole design is about never letting an empty answer mean "nothing happened", that was the one
    /// place the read did exactly that.</para>
    ///
    /// <para>Rare but genuinely reachable: PostgreSQL's deadlock detector resolves cycles, but only after
    /// <c>deadlock_timeout</c> (1s by default), and a capture can land inside that window. When it does, this
    /// is the only evidence that will ever exist — the edges are stored, and the engine kills one of the
    /// participants a moment later.</para>
    ///
    /// <para>Detected by reachability rather than by "the collection has no root", which would miss a cycle
    /// sharing a capture with an ordinary chain. Recursion stops as soon as a walk returns to where it
    /// started (<c>at_pid &lt;&gt; start_pid</c>), refuses to wander into a foreign cycle, and is
    /// depth-capped besides.</para>
    ///
    /// <para><b>One row per CYCLE, not per capture, and the attributed names come from the cycle's own
    /// edges.</b> Both of those were wrong first time and both failed the same way — silently, with a
    /// plausible number. Grouping on <c>collection_id</c> alone merged two independent deadlocks that landed
    /// in one sample into a single bogus component; joining the edge rows on <c>collection_id</c> alone
    /// aggregated <c>database_name</c> over every edge in the capture, so a cycle sharing a sample with an
    /// unrelated chain reported whichever database sorted first. Each walk's <c>members</c> array is carried
    /// specifically so the component can be canonicalised (sorted, then DISTINCT collapses the rotations one
    /// per participant) and used to scope the join.</para>
    ///
    /// <para>$1 server_id, $2/$3 window (naive UTC), $4 row limit.</para>
    /// </summary>
    public const string PgBlockingCyclesSql = """
        WITH RECURSIVE edges AS (
            SELECT
                collection_id,
                collection_time,
                blocked_pid,
                blocking_pid,
                database_name,
                blocked_application_name
            FROM pg_blocking_edges
            WHERE server_id = $1
            AND   collection_time >= $2
            AND   collection_time <= $3
        ),
        walk AS (
            SELECT
                collection_id,
                blocked_pid AS start_pid,
                blocking_pid AS at_pid,
                1 AS depth,
                ARRAY[blocked_pid] AS members
            FROM edges

            UNION ALL

            SELECT
                w.collection_id,
                w.start_pid,
                e.blocking_pid,
                w.depth + 1,
                w.members || e.blocked_pid
            FROM walk AS w
            JOIN edges AS e
              ON  e.collection_id = w.collection_id
              AND e.blocked_pid = w.at_pid
            WHERE w.depth < 32
            /* Stop the moment the walk closes on where it started — that IS the detection. */
            AND   w.at_pid <> w.start_pid
            /* And never wander into a FOREIGN cycle: without this, a walk that starts outside a cycle and
               reaches one loops inside it to the depth cap, doing 32 rounds of work per starting edge. */
            AND   (e.blocking_pid = w.start_pid OR e.blocking_pid <> ALL(w.members))
        ),
        closed AS (
            /* A walk that returned to its own start. Its `members` array is exactly that cycle's
               participants, which is why the array is carried at all. */
            SELECT collection_id, members
            FROM walk
            WHERE at_pid = start_pid
        ),
        components AS (
            /* Canonicalise: every participant of one cycle produces its own closed walk with the same
               member SET in a rotated order, so sorting collapses them to one row per actual cycle.
               DISTINCT then dedupes the rotations.

               Grouping by the component rather than by the capture is load-bearing: two independent
               deadlocks landing in the same one-minute capture are two findings, and grouping on
               collection_id alone merged their pids into one bogus connected component. */
            SELECT DISTINCT
                collection_id,
                (SELECT array_agg(m ORDER BY m) FROM unnest(members) AS m) AS members
            FROM closed
        )
        SELECT
            e.collection_time                        AS captured_at,
            cardinality(c.members)                   AS participant_count,
            c.members                                AS pids,
            min(e.database_name)                     AS database_name,
            min(e.blocked_application_name)          AS application_name
        FROM components AS c
        JOIN edges AS e
          ON  e.collection_id = c.collection_id
          /* Scoped to the cycle's OWN participants. Joining on collection_id alone aggregated
             database_name and application_name over every edge in the capture, so a cycle in one database
             sharing a sample with an ordinary chain in another reported whichever name sorted first —
             pointing the reader at a database the deadlock never touched. */
          AND e.blocked_pid = ANY(c.members)
        GROUP BY e.collection_id, e.collection_time, c.members
        ORDER BY e.collection_time DESC
        LIMIT $4
        """;

    public static async Task<List<PgBlockingCycleRow>> GetPgBlockingCyclesAsync(
        NpgsqlDataSource postgres, int serverId, DateTime startUtc, DateTime endUtc, int limit,
        CancellationToken cancellationToken = default)
    {
        var rows = new List<PgBlockingCycleRow>();
        await using var command = postgres.CreateCommand(PgBlockingCyclesSql);
        command.Parameters.AddWithValue(serverId);
        /* SpecifyKind(Unspecified), not the bare value. Npgsql does not reject Kind=Utc — it infers
           timestamptz, and PostgreSQL then zone-shifts the window against the store's NAIVE timestamp
           columns, so east of UTC the window silently slides off the data. Same convention as every
           other PostgreSQL read (DarlingPgXminReader, and the alert adapter's NaiveUtcNow). */
        command.Parameters.AddWithValue(DateTime.SpecifyKind(startUtc, DateTimeKind.Unspecified));
        command.Parameters.AddWithValue(DateTime.SpecifyKind(endUtc, DateTimeKind.Unspecified));
        command.Parameters.AddWithValue(limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new PgBlockingCycleRow(
                reader.GetDateTime(0),
                reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                reader.IsDBNull(2) ? Array.Empty<int>() : reader.GetFieldValue<int[]>(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return rows;
    }

    /// <summary>
    /// How many captures in the window recorded any blocking at all, and how many recorded none.
    /// <para>Reported alongside the chains because the denominator is the honest part of a sampled signal.
    /// "Three chains" means something different in a window of 60 captures than in a window of 4, and the
    /// stored table cannot say which on its own — an absent capture and a capture that found nothing look
    /// identical in a table that only holds edges. The blocking-free count comes from
    /// <c>collection_log</c>, which records a SUCCESS with zero rows, so the two really are
    /// distinguishable — but only by looking there.</para>
    /// <para>$1 server_id, $2/$3 window (naive UTC).</para>
    /// </summary>
    public const string PgBlockingCaptureCountsSql = """
        SELECT
            count(*) FILTER (WHERE l.rows_collected > 0),
            count(*),
            min(l.collection_time),
            max(l.collection_time)
        FROM collection_log AS l
        WHERE l.server_id = $1
        AND   l.collector_name = 'pg_blocking'
        AND   l.status = 'SUCCESS'
        AND   l.collection_time >= $2
        AND   l.collection_time <= $3
        """;

    public sealed record PgBlockingCaptureCounts(
        long CapturesWithBlocking,
        long CapturesTotal,
        DateTime? FirstCaptureAt,
        DateTime? LastCaptureAt);

    public static async Task<PgBlockingCaptureCounts> GetPgBlockingCaptureCountsAsync(
        NpgsqlDataSource postgres, int serverId, DateTime startUtc, DateTime endUtc,
        CancellationToken cancellationToken = default)
    {
        await using var command = postgres.CreateCommand(PgBlockingCaptureCountsSql);
        command.Parameters.AddWithValue(serverId);
        /* SpecifyKind(Unspecified), not the bare value. Npgsql does not reject Kind=Utc — it infers
           timestamptz, and PostgreSQL then zone-shifts the window against the store's NAIVE timestamp
           columns, so east of UTC the window silently slides off the data. Same convention as every
           other PostgreSQL read (DarlingPgXminReader, and the alert adapter's NaiveUtcNow). */
        command.Parameters.AddWithValue(DateTime.SpecifyKind(startUtc, DateTimeKind.Unspecified));
        command.Parameters.AddWithValue(DateTime.SpecifyKind(endUtc, DateTimeKind.Unspecified));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return new PgBlockingCaptureCounts(
                reader.IsDBNull(0) ? 0 : reader.GetInt64(0),
                reader.IsDBNull(1) ? 0 : reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetDateTime(2),
                reader.IsDBNull(3) ? null : reader.GetDateTime(3));
        }

        return new PgBlockingCaptureCounts(0, 0, null, null);
    }
}
