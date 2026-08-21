using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using PerformanceMonitor.Analysis;
using PerformanceMonitor.Notifications;
using PerformanceMonitorLite.Database;

namespace PerformanceMonitorLite.Analysis;

/// <summary>
/// Persists analysis findings to DuckDB and checks for muted story hashes.
/// Handles the write side of the analysis pipeline — after the engine produces
/// stories, FindingStore saves them and filters out muted patterns.
///
/// <para>
/// The write is two-phase (mirrors Darling's <c>PgFindingStore</c>): <see cref="FilterMutedFindingsAsync"/>
/// materializes the surviving findings WITHOUT inserting, the orchestrator enriches them with drill-down
/// and builds each finding's <see cref="AnalysisFinding.Remediation"/>, then <see cref="InsertFindingsAsync"/>
/// persists the rows — including the BUILT action serialized as <c>remediation_action_json</c> via the
/// shared <see cref="AlertContextSerializer"/>. The read paths deserialize it back onto each finding so the
/// Recommendations reader can render the copy-paste command byte-identically to the Darling viewer.
/// <see cref="SaveFindingsAsync"/> remains as a single-pass convenience wrapper (no action attached).
/// </para>
/// </summary>
public class FindingStore
{
    private readonly DuckDbInitializer _duckDb;
    private long _nextId;

    public FindingStore(DuckDbInitializer duckDb)
    {
        _duckDb = duckDb;
        _nextId = DateTime.UtcNow.Ticks;
    }

    /// <summary>
    /// Mute-filters the stories and materializes the SURVIVING findings WITHOUT inserting them
    /// (mirrors Darling's <c>PgFindingStore.FilterMutedFindingsAsync</c> — the recommendations
    /// rebuild D2/P2 reorder). The orchestrator then enriches these survivors with drill-down and
    /// builds + attaches each finding's <see cref="AnalysisFinding.Remediation"/> before calling
    /// <see cref="InsertFindingsAsync"/>, so the BUILT action is persisted on the row (the builders
    /// require a drill-down the store read-back does not return). Absolution stories (severity 0)
    /// and muted hashes are dropped here and never enriched.
    /// </summary>
    public async Task<List<AnalysisFinding>> FilterMutedFindingsAsync(
        List<AnalysisStory> stories, AnalysisContext context)
    {
        var analysisTime = DateTime.UtcNow;
        var survivors = new List<AnalysisFinding>();

        /* One read lock + one connection for the mute-filter read only. Released before the caller
           enriches + builds actions; InsertFindingsAsync then re-acquires for the batched insert.
           The lock is NoRecursion, so the helper below operates on the passed connection. */
        using var readLock = _duckDb.AcquireReadLock(context.CancellationToken);
        using var connection = _duckDb.CreateConnection();
        await connection.OpenAsync(context.CancellationToken);

        var mutedHashes = await GetMutedHashesAsync(connection, context.ServerId, context.CancellationToken);

        foreach (var story in stories)
        {
            // Skip absolution stories (severity 0) — they confirm health, not problems
            if (story.Severity <= 0)
                continue;

            if (mutedHashes.Contains(story.StoryPathHash))
                continue;

            survivors.Add(new AnalysisFinding
            {
                FindingId = _nextId++,
                AnalysisTime = analysisTime,
                ServerId = context.ServerId,
                ServerName = context.ServerName,
                DatabaseName = story.DatabaseName,
                TimeRangeStart = context.TimeRangeStart,
                TimeRangeEnd = context.TimeRangeEnd,
                Severity = story.Severity,
                Confidence = story.Confidence,
                Category = story.Category,
                StoryPath = story.StoryPath,
                StoryPathHash = story.StoryPathHash,
                IncidentId = story.IncidentId,
                StoryText = story.StoryText,
                RootFactKey = story.RootFactKey,
                RootFactValue = story.RootFactValue,
                LeafFactKey = story.LeafFactKey,
                LeafFactValue = story.LeafFactValue,
                FactCount = story.FactCount,
                RootFactMetadata = story.RootFactMetadata
            });
        }

        return survivors;
    }

    /// <summary>
    /// Inserts the (already mute-filtered, enriched, and action-attached) findings in one batched
    /// pass on a single connection. Each row persists its BUILT <see cref="AnalysisFinding.Remediation"/>
    /// as <c>remediation_action_json</c> via the shared <see cref="AlertContextSerializer"/>, so a Lite
    /// finding's persisted action round-trips byte-identically to a Darling / Dashboard one. Returns the
    /// same list for caller convenience; the in-memory findings are unchanged.
    ///
    /// <para>#2443: the read lock and the connection open are the LAST cancellation points on this
    /// pass, and that is a decision rather than an oversight. Past them the batch runs to completion,
    /// because the third outcome — a half-written finding set — is worse than the one the pass
    /// already reports. There is no transaction here, every row in a batch shares one
    /// <c>analysis_time</c>, and <see cref="GetLatestFindingsAsync"/> reads the newest
    /// <c>analysis_time</c> — so a batch cut in half does not read as truncated, it reads as a
    /// complete analysis that found fewer problems. The server would look HEALTHIER for having been
    /// abandoned, with nothing marking the set incomplete. Cancelling before the first row costs this
    /// cycle's findings and says so; cancelling after it silently changes what the store means.</para>
    ///
    /// <para>The tail is affordable to finish: N small INSERTs into an embedded file in this process.
    /// It is not where a pass wedges. Same call the Darling twin makes, and the same call
    /// <c>AnalysisService</c> made a layer up in #2419 — "the post-enrichment tail carries no check
    /// on purpose" — restated at the write it protects.</para>
    /// </summary>
    public async Task<List<AnalysisFinding>> InsertFindingsAsync(
        List<AnalysisFinding> findings, AnalysisContext context)
    {
        if (findings.Count == 0)
            return findings;

        /* One read lock + one connection for the whole batch, reused for every insert. */
        using var readLock = _duckDb.AcquireReadLock(context.CancellationToken);
        using var connection = _duckDb.CreateConnection();
        await connection.OpenAsync(context.CancellationToken);

        /* No token check between rows: see the note above. Once the first row is written this batch
           is finishing. */
        foreach (var finding in findings)
            await InsertFindingAsync(connection, finding);

        return findings;
    }

    /// <summary>
    /// Saves analysis stories as findings in one pass, filtering out any that match muted hashes —
    /// the single-pass convenience surface. Implemented as <see cref="FilterMutedFindingsAsync"/> +
    /// <see cref="InsertFindingsAsync"/>. NOTE: a caller that wants <c>remediation_action_json</c>
    /// persisted must use the two-phase shape and attach actions between the phases (as
    /// <c>AnalysisService</c> does); this single pass inserts the findings exactly as filtered, with
    /// a null action.
    /// </summary>
    public async Task<List<AnalysisFinding>> SaveFindingsAsync(
        List<AnalysisStory> stories, AnalysisContext context)
    {
        var survivors = await FilterMutedFindingsAsync(stories, context);
        return await InsertFindingsAsync(survivors, context);
    }

    /// <summary>
    /// Returns the most recent findings for a server within the given time range.    ///
    /// <para>#2443 exempt: off the analysis pass. This surface serves the viewer, the MCP and the
    /// retention sweep — lifetimes with no per-pass budget and no wedged analysis to abandon — so
    /// its store calls take no pass token. Threading one here would mean inventing a caller that
    /// does not exist. Matches the Darling twin's PgFindingStore exactly.</para>
    /// </summary>
    public async Task<List<AnalysisFinding>> GetRecentFindingsAsync(
        int serverId, int hoursBack = 24, int limit = 100)
    {
        var findings = new List<AnalysisFinding>();

        using var readLock = _duckDb.AcquireReadLock();
        using var connection = _duckDb.CreateConnection();
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT finding_id, analysis_time, server_id, server_name, database_name,
       time_range_start, time_range_end, severity, confidence, category,
       story_path, story_path_hash, story_text,
       root_fact_key, root_fact_value, leaf_fact_key, leaf_fact_value, fact_count, incident_id,
       remediation_action_json, drill_down_json
FROM analysis_findings
WHERE server_id = $1
AND   analysis_time >= $2
ORDER BY analysis_time DESC, severity DESC
LIMIT $3";

        cmd.Parameters.Add(new DuckDBParameter { Value = serverId });
        cmd.Parameters.Add(new DuckDBParameter { Value = DateTime.UtcNow.AddHours(-hoursBack) });
        cmd.Parameters.Add(new DuckDBParameter { Value = limit });

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            findings.Add(new AnalysisFinding
            {
                FindingId = reader.GetInt64(0),
                AnalysisTime = reader.GetDateTime(1),
                ServerId = reader.GetInt32(2),
                ServerName = reader.GetString(3),
                DatabaseName = reader.IsDBNull(4) ? null : reader.GetString(4),
                TimeRangeStart = reader.IsDBNull(5) ? null : reader.GetDateTime(5),
                TimeRangeEnd = reader.IsDBNull(6) ? null : reader.GetDateTime(6),
                Severity = reader.GetDouble(7),
                Confidence = reader.GetDouble(8),
                Category = reader.GetString(9),
                StoryPath = reader.GetString(10),
                StoryPathHash = reader.GetString(11),
                StoryText = reader.GetString(12),
                RootFactKey = reader.GetString(13),
                RootFactValue = reader.IsDBNull(14) ? null : reader.GetDouble(14),
                LeafFactKey = reader.IsDBNull(15) ? null : reader.GetString(15),
                LeafFactValue = reader.IsDBNull(16) ? null : reader.GetDouble(16),
                FactCount = reader.GetInt32(17),
                IncidentId = reader.IsDBNull(18) ? string.Empty : reader.GetString(18),
                // Persisted BUILT action (the recommendations copy-paste command) deserialized via the
                // SAME shared serializer the alert path uses; null/garbage degrades to "no command".
                Remediation = reader.IsDBNull(19) ? null : AlertContextSerializer.DeserializeAction(reader.GetString(19)),
                DrillDown = reader.IsDBNull(20) ? null : DrillDownSerializer.Deserialize(reader.GetString(20))
            });
        }

        return findings;
    }

    /// <summary>
    /// Returns the latest analysis run's findings for a server (most recent analysis_time).    ///
    /// <para>#2443 exempt: off the analysis pass. This surface serves the viewer, the MCP and the
    /// retention sweep — lifetimes with no per-pass budget and no wedged analysis to abandon — so
    /// its store calls take no pass token. Threading one here would mean inventing a caller that
    /// does not exist. Matches the Darling twin's PgFindingStore exactly.</para>
    /// </summary>
    public async Task<List<AnalysisFinding>> GetLatestFindingsAsync(int serverId)
    {
        var findings = new List<AnalysisFinding>();

        using var readLock = _duckDb.AcquireReadLock();
        using var connection = _duckDb.CreateConnection();
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT finding_id, analysis_time, server_id, server_name, database_name,
       time_range_start, time_range_end, severity, confidence, category,
       story_path, story_path_hash, story_text,
       root_fact_key, root_fact_value, leaf_fact_key, leaf_fact_value, fact_count, incident_id,
       remediation_action_json, drill_down_json
FROM analysis_findings
WHERE server_id = $1
AND   analysis_time = (
    SELECT MAX(analysis_time) FROM analysis_findings WHERE server_id = $1
)
ORDER BY severity DESC";

        cmd.Parameters.Add(new DuckDBParameter { Value = serverId });

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            findings.Add(new AnalysisFinding
            {
                FindingId = reader.GetInt64(0),
                AnalysisTime = reader.GetDateTime(1),
                ServerId = reader.GetInt32(2),
                ServerName = reader.GetString(3),
                DatabaseName = reader.IsDBNull(4) ? null : reader.GetString(4),
                TimeRangeStart = reader.IsDBNull(5) ? null : reader.GetDateTime(5),
                TimeRangeEnd = reader.IsDBNull(6) ? null : reader.GetDateTime(6),
                Severity = reader.GetDouble(7),
                Confidence = reader.GetDouble(8),
                Category = reader.GetString(9),
                StoryPath = reader.GetString(10),
                StoryPathHash = reader.GetString(11),
                StoryText = reader.GetString(12),
                RootFactKey = reader.GetString(13),
                RootFactValue = reader.IsDBNull(14) ? null : reader.GetDouble(14),
                LeafFactKey = reader.IsDBNull(15) ? null : reader.GetString(15),
                LeafFactValue = reader.IsDBNull(16) ? null : reader.GetDouble(16),
                FactCount = reader.GetInt32(17),
                IncidentId = reader.IsDBNull(18) ? string.Empty : reader.GetString(18),
                // Persisted BUILT action (the recommendations copy-paste command) deserialized via the
                // SAME shared serializer the alert path uses; null/garbage degrades to "no command".
                Remediation = reader.IsDBNull(19) ? null : AlertContextSerializer.DeserializeAction(reader.GetString(19)),
                DrillDown = reader.IsDBNull(20) ? null : DrillDownSerializer.Deserialize(reader.GetString(20))
            });
        }

        return findings;
    }

    /// <summary>
    /// Mutes a story pattern so it won't appear in future analysis runs.    ///
    /// <para>#2443 exempt: off the analysis pass. This surface serves the viewer, the MCP and the
    /// retention sweep — lifetimes with no per-pass budget and no wedged analysis to abandon — so
    /// its store calls take no pass token. Threading one here would mean inventing a caller that
    /// does not exist. Matches the Darling twin's PgFindingStore exactly.</para>
    /// </summary>
    public async Task MuteStoryAsync(int serverId, string storyPathHash, string storyPath, string? reason = null)
    {
        using var readLock = _duckDb.AcquireReadLock();
        using var connection = _duckDb.CreateConnection();
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
INSERT INTO analysis_muted (mute_id, server_id, story_path_hash, story_path, muted_date, reason)
VALUES ($1, $2, $3, $4, $5, $6)";

        cmd.Parameters.Add(new DuckDBParameter { Value = _nextId++ });
        // serverId 0 is the MCP "mute across all servers" sentinel; persist it as NULL, the
        // canonical global marker every reader filters on (legacy 0 rows are still honored).
        cmd.Parameters.Add(new DuckDBParameter { Value = serverId == 0 ? (object)DBNull.Value : serverId });
        cmd.Parameters.Add(new DuckDBParameter { Value = storyPathHash });
        cmd.Parameters.Add(new DuckDBParameter { Value = storyPath });
        cmd.Parameters.Add(new DuckDBParameter { Value = DateTime.UtcNow });
        cmd.Parameters.Add(new DuckDBParameter { Value = reason ?? (object)DBNull.Value });

        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Cleans up old findings beyond the retention period.    ///
    /// <para>#2443 exempt: off the analysis pass. This surface serves the viewer, the MCP and the
    /// retention sweep — lifetimes with no per-pass budget and no wedged analysis to abandon — so
    /// its store calls take no pass token. Threading one here would mean inventing a caller that
    /// does not exist. Matches the Darling twin's PgFindingStore exactly.</para>
    /// </summary>
    public async Task CleanupOldFindingsAsync(int retentionDays = 30)
    {
        using var readLock = _duckDb.AcquireReadLock();
        using var connection = _duckDb.CreateConnection();
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM analysis_findings WHERE analysis_time < $1";
        cmd.Parameters.Add(new DuckDBParameter { Value = DateTime.UtcNow.AddDays(-retentionDays) });
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Reads muted story hashes on an already-open connection. The caller owns the read
    /// lock and connection (NoRecursion lock — do not re-acquire here). Used by
    /// FilterMutedFindingsAsync so the mute-filter read reuses that phase's connection.
    /// </summary>
    private static async Task<HashSet<string>> GetMutedHashesAsync(
        DuckDBConnection connection, int serverId, CancellationToken cancellationToken)
    {
        var hashes = new HashSet<string>();

        using var cmd = connection.CreateCommand();
        // server_id = 0 rows are legacy all-servers mutes written by the pre-fix MCP tool path
        // (no real server has id 0); honor them as global, alongside the canonical NULL.
        cmd.CommandText = @"
SELECT story_path_hash FROM analysis_muted
WHERE server_id = $1 OR server_id IS NULL OR server_id = 0";

        cmd.Parameters.Add(new DuckDBParameter { Value = serverId });

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            hashes.Add(reader.GetString(0));

        return hashes;
    }

    /// <summary>
    /// Inserts one finding on an already-open connection. The caller owns the read lock
    /// and connection, so a batch of inserts in one InsertFindingsAsync call shares a
    /// single lock acquisition and connection.
    ///
    /// <para>#2443 exempt: this write deliberately takes no token. Cancelling inside a single-row
    /// INSERT buys nothing — the row is microseconds of work in-process — and costs a definite
    /// answer about whether it landed: DuckDB's cancel is <c>duckdb_interrupt</c>, so an interrupted
    /// INSERT can leave a row that did commit, in a set nothing marks as partial.
    /// <see cref="InsertFindingsAsync"/> carries the full reasoning and the abandonment point that
    /// replaces this one.</para>
    /// </summary>
    private static async Task InsertFindingAsync(DuckDBConnection connection, AnalysisFinding finding)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
INSERT INTO analysis_findings
    (finding_id, analysis_time, server_id, server_name, database_name,
     time_range_start, time_range_end, severity, confidence, category,
     story_path, story_path_hash, story_text,
     root_fact_key, root_fact_value, leaf_fact_key, leaf_fact_value, fact_count, incident_id,
     remediation_action_json, drill_down_json)
VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18, $19, $20, $21)";

        cmd.Parameters.Add(new DuckDBParameter { Value = finding.FindingId });
        cmd.Parameters.Add(new DuckDBParameter { Value = finding.AnalysisTime });
        cmd.Parameters.Add(new DuckDBParameter { Value = finding.ServerId });
        cmd.Parameters.Add(new DuckDBParameter { Value = finding.ServerName });
        cmd.Parameters.Add(new DuckDBParameter { Value = finding.DatabaseName ?? (object)DBNull.Value });
        cmd.Parameters.Add(new DuckDBParameter { Value = finding.TimeRangeStart ?? (object)DBNull.Value });
        cmd.Parameters.Add(new DuckDBParameter { Value = finding.TimeRangeEnd ?? (object)DBNull.Value });
        cmd.Parameters.Add(new DuckDBParameter { Value = finding.Severity });
        cmd.Parameters.Add(new DuckDBParameter { Value = finding.Confidence });
        cmd.Parameters.Add(new DuckDBParameter { Value = finding.Category });
        cmd.Parameters.Add(new DuckDBParameter { Value = finding.StoryPath });
        cmd.Parameters.Add(new DuckDBParameter { Value = finding.StoryPathHash });
        cmd.Parameters.Add(new DuckDBParameter { Value = finding.StoryText });
        cmd.Parameters.Add(new DuckDBParameter { Value = finding.RootFactKey });
        cmd.Parameters.Add(new DuckDBParameter { Value = finding.RootFactValue ?? (object)DBNull.Value });
        cmd.Parameters.Add(new DuckDBParameter { Value = finding.LeafFactKey ?? (object)DBNull.Value });
        cmd.Parameters.Add(new DuckDBParameter { Value = finding.LeafFactValue ?? (object)DBNull.Value });
        cmd.Parameters.Add(new DuckDBParameter { Value = finding.FactCount });
        cmd.Parameters.Add(new DuckDBParameter { Value = finding.IncidentId ?? string.Empty });
        // Persist the BUILT action (mirrors the alert path's ContextJson) so the Recommendations
        // reader can render the copy-paste command from a stored finding. Null when no shape applies.
        cmd.Parameters.Add(new DuckDBParameter { Value = (object?)AlertContextSerializer.SerializeAction(finding.Remediation) ?? DBNull.Value });
        // #2060: the CAPPED drill-down beside the built action — same rationale (the evidence rows
        // exist only on the write path), same degrade-to-NULL discipline.
        cmd.Parameters.Add(new DuckDBParameter { Value = (object?)DrillDownSerializer.Serialize(finding.DrillDown) ?? DBNull.Value });

        await cmd.ExecuteNonQueryAsync();
    }
}
