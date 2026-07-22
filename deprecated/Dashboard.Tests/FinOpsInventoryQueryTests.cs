using System;
using PerformanceMonitorDashboard.Services;
using Xunit;

namespace PerformanceMonitorDashboard.Tests;

/// <summary>
/// #1535 regression guard (Dashboard mirror of Lite's FinOpsInventoryQueryTests): the FinOps live
/// server-inventory query must read edition/version/storage from permission-free scalars and dynamic
/// SQL only — never <c>sys.dm_os_sys_info</c> (VIEW DATABASE STATE on Azure SQL DB), and never a
/// hard-coded <c>sys.master_files</c> (absent on Azure SQL DB) without an engine-edition branch. The
/// hardware read that genuinely needs the DMV stays isolated in its own best-effort query so a
/// permission gap loses only CPU/memory/socket facts, not the whole inventory row.
/// </summary>
public class FinOpsInventoryQueryTests
{
    [Fact]
    public void InventoryQuery_DoesNotDependOnDmOsSysInfo()
    {
        Assert.DoesNotContain("dm_os_sys_info", DatabaseService.InventoryQueryText, StringComparison.Ordinal);
    }

    [Fact]
    public void HardwareQuery_IsolatesTheDmvRead()
    {
        Assert.Contains("sys.dm_os_sys_info", DatabaseService.HardwareQueryText, StringComparison.Ordinal);
        // The hardware-only columns must ride the isolated read, not the inventory query.
        Assert.Contains("cpu_count", DatabaseService.HardwareQueryText, StringComparison.Ordinal);
        Assert.Contains("sqlserver_start_time", DatabaseService.HardwareQueryText, StringComparison.Ordinal);
    }

    [Fact]
    public void InventoryQuery_StillDetectsEditionAndStaysAzureSafeForStorage()
    {
        // Edition/version classification survives on permission-free scalars.
        Assert.Contains("SERVERPROPERTY('EngineEdition')", DatabaseService.InventoryQueryText, StringComparison.Ordinal);
        // Storage no longer hard-codes sys.master_files (absent on Azure SQL DB); the engine-edition
        // branch reads sys.database_files there.
        Assert.Contains("sys.database_files", DatabaseService.InventoryQueryText, StringComparison.Ordinal);
    }
}
