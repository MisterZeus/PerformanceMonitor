/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using PerformanceMonitorDashboard.Services;
using Xunit;

namespace PerformanceMonitorDashboard.Tests;

/// <summary>
/// #1535 regression guard (Full Dashboard): the connectivity probe's edition-detection query must
/// read engine edition from permission-free scalars ONLY, never from <c>sys.dm_os_sys_info</c>. The
/// Dashboard rejects Azure SQL DB (EngineEdition 5) as unsupported; before the split, an Azure login
/// lacking VIEW DATABASE STATE threw the combined query, so the edition never registered and the
/// server was mis-handled instead of cleanly rejected with the "use the Lite edition" message. This
/// pins the decoupling; <c>sqlserver_start_time</c> (the one column that needs the DMV) lives in its
/// own best-effort read.
/// </summary>
public class ServerManagerDetectionQueryTests
{
    [Fact]
    public void DetectionQuery_DoesNotDependOnDmOsSysInfo()
    {
        Assert.DoesNotContain("dm_os_sys_info", ServerManager.DetectionQueryText, StringComparison.Ordinal);
        Assert.DoesNotContain("sqlserver_start_time", ServerManager.DetectionQueryText, StringComparison.Ordinal);
    }

    [Fact]
    public void DetectionQuery_DetectsEngineEditionViaServerProperty()
    {
        Assert.Contains("SERVERPROPERTY('EngineEdition')", ServerManager.DetectionQueryText, StringComparison.Ordinal);
    }

    [Fact]
    public void StartTimeQuery_IsolatesTheOnlyReadThatNeedsDmOsSysInfo()
    {
        Assert.Contains("sqlserver_start_time", ServerManager.ServerStartTimeQueryText, StringComparison.Ordinal);
        Assert.Contains("sys.dm_os_sys_info", ServerManager.ServerStartTimeQueryText, StringComparison.Ordinal);
    }
}
