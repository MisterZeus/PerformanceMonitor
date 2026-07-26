using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using PerformanceMonitorLite.Database;
using Xunit;

namespace PerformanceMonitorLite.Tests;

/// <summary>
/// Class fixture that creates one DuckDB database with the full Lite schema per test
/// CLASS instead of per test. DuckDbInitializer.InitializeAsync runs ~80 DDL statements
/// (36 collector tables plus indexes, the archive views, and the analysis schema) and
/// dominated the analysis-heavy suite at roughly 1.4s per test. Tests share the class's
/// database and get pristine DATA via <see cref="ResetData"/> from the test class
/// constructor — xUnit runs tests within a class serially, so the shared database is
/// never touched concurrently. The schema and the schema_version /
/// analysis_schema_version stamps persist for the life of the class, so the database
/// keeps reading as current rather than as a blank file needing migration.
///
/// Deliberately IClassFixture (one database per class), NOT a collection fixture: a
/// single shared collection would serialize these classes against each other and give
/// back most of the win. Each class owns its own database file, keeping xUnit's
/// cross-class parallelism intact.
/// </summary>
public sealed class SharedDuckDbFixture : IAsyncLifetime
{
    private readonly string _tempDir;
    private string[] _dataTables = [];

    public DuckDbInitializer DuckDb { get; }

    public SharedDuckDbFixture()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LiteTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        DuckDb = new DuckDbInitializer(Path.Combine(_tempDir, "test.duckdb"));
    }

    public async ValueTask InitializeAsync()
    {
        await DuckDb.InitializeAsync();

        /* Snapshot the base tables present after a fresh initialization. ResetData deletes rows
           from exactly this set; the two version-stamp tables are excluded so the schema keeps
           reading as current instead of triggering the fresh-database migration path if a test
           ever re-runs InitializeAsync. */
        var tables = new List<string>();
        using var readLock = DuckDb.AcquireReadLock();
        using var connection = DuckDb.CreateConnection();
        await connection.OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT table_name
FROM information_schema.tables
WHERE table_schema = 'main'
AND   table_type = 'BASE TABLE'
AND   table_name NOT IN ('schema_version', 'analysis_schema_version')";
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }
        _dataTables = [.. tables];
    }

    /// <summary>
    /// Returns every data table to its post-initialization state (empty) so each test starts
    /// from pristine data on the shared schema. Called from the test class constructor, which
    /// xUnit runs before every test.
    /// </summary>
    public void ResetData()
    {
        using var readLock = DuckDb.AcquireReadLock();
        using var connection = DuckDb.CreateConnection();
        connection.Open();
        foreach (var table in _dataTables)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"DELETE FROM {table}";
            cmd.ExecuteNonQuery();
        }
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch { /* Best-effort cleanup */ }
        return default;
    }
}
