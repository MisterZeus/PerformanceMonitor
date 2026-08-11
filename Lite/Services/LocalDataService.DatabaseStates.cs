/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System.Collections.Generic;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using PerformanceMonitor.Alerting;

namespace PerformanceMonitorLite.Services;

public partial class LocalDataService
{
    /* Effective state = STANDBY for a read-only log-shipping secondary (is_in_standby), else the raw
       state_desc. A standby secondary reports state_desc = ONLINE with is_in_standby = 1 and flips through
       RESTORING on every log restore; collapsing it to a single stable STANDBY token means it baselines as
       STANDBY and never churns. Composed identically in the seed, the deviation read, the editor and reset. */
    private const string EffectiveStateSql = "CASE WHEN ds.is_in_standby THEN 'STANDBY' ELSE ds.state_desc END";

    /// <summary>
    /// The databases whose collected state deviates from their expected state, for the baseline-deviation
    /// database-state alert. Fires only when the deviation is present in the TWO most recent collections
    /// (so a restart's RECOVERY_PENDING / RECOVERING transients — and a standby secondary's per-restore
    /// RESTORING flicker — don't page unless the condition actually sticks). First AUTO-SEEDS a baseline
    /// (the effective state) for any database in the newest snapshot that has none — EXCEPT an integrity or
    /// transient effective state (<see cref="DatabaseStateTokens.NeverBaselinedSqlList"/>), which is left
    /// pending so onboarding a server mid-outage or mid-restore doesn't learn that state as expected. Then
    /// HEALS an auto-baseline that nonetheless records one of those states — written by an older build, or
    /// by re-baselining a database by hand while it was mid-something — to ONLINE once the database reaches
    /// ONLINE, so it stops deviating by being healthy (#2189); a user override, and an OFFLINE or STANDBY
    /// baseline, are never touched. Also tidies auto-baselines for databases that have dropped off the
    /// newest snapshot (user overrides are preserved). The base table always holds the newest snapshots
    /// (archival only moves older rows to parquet), so it is queried directly.
    /// </summary>
    public async Task<List<DatabaseStateInfo>> GetDatabaseStateDeviationsAsync(int serverId)
    {
        using var connection = await OpenConnectionAsync();

        /* Seed missing baselines from the latest snapshot (insert-if-absent; effective state). An integrity
           or transient state is never learned: a critical first observation stays pending and alerts via the
           no-baseline arm below, and a transient one stays pending SILENTLY until it settles. */
        using (var seed = connection.CreateCommand())
        {
            seed.CommandText = $@"
INSERT INTO config_database_state_expected (server_id, database_name, expected_state, is_user_override, updated_at)
SELECT $1, ds.database_name, {EffectiveStateSql}, false, now()::TIMESTAMP
FROM database_states ds
WHERE ds.server_id = $1
AND   ds.collection_time = (SELECT MAX(collection_time) FROM database_states WHERE server_id = $1)
AND   ds.state_desc IS NOT NULL
AND   {EffectiveStateSql} NOT IN ({DatabaseStateTokens.NeverBaselinedSqlList})
AND   NOT EXISTS (
    SELECT 1 FROM config_database_state_expected e
    WHERE e.server_id = $1 AND e.database_name = ds.database_name
)";
            seed.Parameters.Add(new DuckDBParameter { Value = serverId });
            await seed.ExecuteNonQueryAsync();
        }

        /* #2189: re-learn an ILLEGITIMATE inferred baseline as ONLINE once the database reaches ONLINE — the
           seed's own rule applied after the fact. An expectation recording a state the seed would refuse to
           learn is not a baseline, it is a snapshot of a database mid-something, and left alone it inverts
           the alert permanently. The widened seed above cannot fix that on its own because it only governs
           rows that do not exist yet; this heals the ones already written, by the old seed or by "reset to
           current" pressed during a restore or an outage (that path records whatever it sees, no filter).

           Two gates. is_user_override = false: an operator who declared an expected state meant it, and a
           database parked at expected OFFLINE must still alert when it comes back ONLINE. And the state list
           is NOT "anything that is not ONLINE" — OFFLINE and STANDBY are steady states worth learning, and
           leaving one is real news: a STANDBY secondary that turns up ONLINE has stopped being a secondary
           (somebody recovered it, log shipping is broken), and an auto-OFFLINE database brought up for an
           hour and re-parked would come back deviating forever. Both are this bug's own shape.

           The EFFECTIVE state is what is matched, never state_desc — a standby secondary reports
           state_desc = 'ONLINE' with is_in_standby set, so matching the raw column would re-baseline every
           log-shipping secondary to ONLINE and then alert it forever for being STANDBY. Uncorrelated
           IN (...), like the prune below. */
        using (var heal = connection.CreateCommand())
        {
            heal.CommandText = $@"
UPDATE config_database_state_expected
SET expected_state = 'ONLINE',
    updated_at = now()::TIMESTAMP
WHERE server_id = $1
AND   is_user_override = false
AND   expected_state IN ({DatabaseStateTokens.NeverBaselinedSqlList})
AND   database_name IN (
    SELECT ds.database_name
    FROM database_states ds
    WHERE ds.server_id = $1
    AND   ds.collection_time = (SELECT MAX(collection_time) FROM database_states WHERE server_id = $1)
    AND   {EffectiveStateSql} = 'ONLINE'
)";
            heal.Parameters.Add(new DuckDBParameter { Value = serverId });
            await heal.ExecuteNonQueryAsync();
        }

        /* Tidy auto-baselines for databases no longer in the newest snapshot (dropped/renamed). User
           overrides are kept — an operator's intent shouldn't vanish because a database is briefly gone. */
        using (var prune = connection.CreateCommand())
        {
            prune.CommandText = @"
DELETE FROM config_database_state_expected
WHERE server_id = $1
AND   is_user_override = false
AND   database_name NOT IN (
    SELECT database_name FROM database_states
    WHERE server_id = $1
    AND   collection_time = (SELECT MAX(collection_time) FROM database_states WHERE server_id = $1)
)";
            prune.Parameters.Add(new DuckDBParameter { Value = serverId });
            await prune.ExecuteNonQueryAsync();
        }

        using var command = connection.CreateCommand();
        command.CommandText = $@"
WITH newest AS (
    SELECT MAX(collection_time) AS t FROM database_states WHERE server_id = $1
),
prev AS (
    SELECT MAX(collection_time) AS t FROM database_states
    WHERE server_id = $1 AND collection_time < (SELECT t FROM newest)
),
latest AS (
    SELECT ds.database_name, {EffectiveStateSql} AS eff
    FROM database_states ds
    WHERE ds.server_id = $1 AND ds.collection_time = (SELECT t FROM newest)
),
previous AS (
    SELECT ds.database_name, {EffectiveStateSql} AS eff
    FROM database_states ds
    WHERE ds.server_id = $1 AND ds.collection_time = (SELECT t FROM prev)
)
SELECT l.database_name, l.eff, COALESCE(e.expected_state, '')
FROM latest l
JOIN previous p
  ON p.database_name = l.database_name
LEFT JOIN config_database_state_expected e
  ON  e.server_id = $1
  AND e.database_name = l.database_name
WHERE (e.expected_state IS NULL
        AND l.eff IN ({DatabaseStateTokens.CriticalSqlList})
        AND p.eff IN ({DatabaseStateTokens.CriticalSqlList}))
   OR (e.expected_state IS NOT NULL AND e.expected_state <> '(ignore)'
        AND l.eff IS DISTINCT FROM e.expected_state
        AND p.eff IS DISTINCT FROM e.expected_state)
ORDER BY l.database_name";
        command.Parameters.Add(new DuckDBParameter { Value = serverId });

        var items = new List<DatabaseStateInfo>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            items.Add(new DatabaseStateInfo
            {
                DatabaseName = reader.IsDBNull(0) ? "" : reader.GetString(0),
                StateDesc = reader.IsDBNull(1) ? "" : reader.GetString(1),
                ExpectedState = reader.IsDBNull(2) ? "" : reader.GetString(2)
            });
        }

        return items;
    }

    /// <summary>
    /// Every database in the latest <c>database_states</c> snapshot for the override editor: its current
    /// EFFECTIVE state joined to its expected state (and whether that expected state is a user override vs
    /// the auto-seeded baseline). Seeds/prunes first via the alert read so the editor and the alert always
    /// agree on what "expected" is.
    /// </summary>
    public async Task<List<DatabaseStateExpectedRow>> GetDatabaseStateExpectationsAsync(int serverId)
    {
        /* Reuse the seed/prune side-effect so the editor shows a baseline for every current database. */
        await GetDatabaseStateDeviationsAsync(serverId);

        using var connection = await OpenConnectionAsync();
        using var command = connection.CreateCommand();
        command.CommandText = $@"
WITH latest AS (
    SELECT ds.database_name, {EffectiveStateSql} AS eff
    FROM database_states ds
    WHERE ds.server_id = $1
    AND   ds.collection_time = (SELECT MAX(collection_time) FROM database_states WHERE server_id = $1)
)
SELECT
    l.database_name,
    l.eff,
    e.expected_state,
    e.is_user_override
FROM latest l
LEFT JOIN config_database_state_expected e
  ON  e.server_id = $1
  AND e.database_name = l.database_name
ORDER BY l.database_name";
        command.Parameters.Add(new DuckDBParameter { Value = serverId });

        var items = new List<DatabaseStateExpectedRow>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            items.Add(new DatabaseStateExpectedRow
            {
                DatabaseName = reader.IsDBNull(0) ? "" : reader.GetString(0),
                CurrentState = reader.IsDBNull(1) ? "" : reader.GetString(1),
                ExpectedState = reader.IsDBNull(2) ? "" : reader.GetString(2),
                IsUserOverride = !reader.IsDBNull(3) && reader.GetBoolean(3)
            });
        }

        return items;
    }

    /// <summary>
    /// Sets a database's expected state (the override) — pass <see cref="DatabaseStateTokens.Ignore"/>
    /// to opt the database out of the alert. Upserts on (server_id, database_name) and marks the row a
    /// user override so a later auto-seed never clobbers it.
    /// </summary>
    public async Task SetDatabaseStateExpectedAsync(int serverId, string databaseName, string expectedState)
    {
        using var connection = await OpenConnectionAsync();
        using var command = connection.CreateCommand();
        /* now()::TIMESTAMP, not a bare current_timestamp: DuckDB resolves a bare current_timestamp against
           the table's columns (and errors) inside a VALUES row and an ON CONFLICT DO UPDATE SET, so the
           override write uses the function form — which binds as an expression everywhere. INSERT ... SELECT
           (not VALUES) for the same reason, matching the auto-seed and reset shape. */
        command.CommandText = @"
INSERT INTO config_database_state_expected (server_id, database_name, expected_state, is_user_override, updated_at)
SELECT $1, $2, $3, true, now()::TIMESTAMP
ON CONFLICT (server_id, database_name)
DO UPDATE SET expected_state = EXCLUDED.expected_state, is_user_override = true, updated_at = now()::TIMESTAMP";
        command.Parameters.Add(new DuckDBParameter { Value = serverId });
        command.Parameters.Add(new DuckDBParameter { Value = databaseName });
        command.Parameters.Add(new DuckDBParameter { Value = expectedState });
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Re-baselines a database: sets its expected state to its current EFFECTIVE state and clears the
    /// user-override flag, so the alert stops firing for a state the operator has accepted as the new normal.
    /// </summary>
    public async Task ResetDatabaseStateExpectedToCurrentAsync(int serverId, string databaseName)
    {
        using var connection = await OpenConnectionAsync();
        using var command = connection.CreateCommand();
        command.CommandText = $@"
INSERT INTO config_database_state_expected (server_id, database_name, expected_state, is_user_override, updated_at)
SELECT $1, $2, {EffectiveStateSql}, false, now()::TIMESTAMP
FROM database_states ds
WHERE ds.server_id = $1
AND   ds.database_name = $2
AND   ds.collection_time = (SELECT MAX(collection_time) FROM database_states WHERE server_id = $1)
ON CONFLICT (server_id, database_name)
DO UPDATE SET expected_state = EXCLUDED.expected_state, is_user_override = false, updated_at = now()::TIMESTAMP";
        command.Parameters.Add(new DuckDBParameter { Value = serverId });
        command.Parameters.Add(new DuckDBParameter { Value = databaseName });
        await command.ExecuteNonQueryAsync();
    }
}

/// <summary>
/// One row of the database-state override editor: a database's current effective state alongside its
/// expected state (auto-seeded baseline or user override).
/// </summary>
public sealed class DatabaseStateExpectedRow
{
    public string DatabaseName { get; set; } = "";
    public string CurrentState { get; set; } = "";
    public string ExpectedState { get; set; } = "";
    public bool IsUserOverride { get; set; }

    public bool IsIgnored => ExpectedState == PerformanceMonitor.Alerting.DatabaseStateTokens.Ignore;

    /// <summary>True when the current state differs from the expected state and the DB isn't ignored.</summary>
    public bool IsDeviating => !IsIgnored && !string.Equals(CurrentState, ExpectedState, System.StringComparison.Ordinal);
}
