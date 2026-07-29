using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using PerformanceMonitorDashboard.Interfaces;
using PerformanceMonitorDashboard.Models;
using PerformanceMonitorDashboard.Services;

namespace PerformanceMonitorDashboard.Mcp;

/// <summary>
/// Lazily creates and caches DatabaseService instances per server for MCP tool access.
/// Thread-safe for concurrent MCP requests.
/// </summary>
public sealed class DatabaseServiceRegistry : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, DatabaseService> _services = new(StringComparer.OrdinalIgnoreCase);
    private readonly ICredentialService _credentialService;

    public DatabaseServiceRegistry(ICredentialService credentialService)
    {
        _credentialService = credentialService;
    }

    /// <summary>
    /// Gets or creates a DatabaseService for the given server connection.
    /// </summary>
    public DatabaseService GetOrCreate(ServerConnection server)
    {
        return _services.GetOrAdd(server.Id, _ =>
        {
            var connectionString = server.GetConnectionString(_credentialService);
            return new DatabaseService(connectionString);
        });
    }

    public ValueTask DisposeAsync()
    {
        _services.Clear();
        return ValueTask.CompletedTask;
    }
}
