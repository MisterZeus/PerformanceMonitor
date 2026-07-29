/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

namespace PerformanceMonitor.Common;

/// <summary>
/// Turns the bare "permission was denied on object 'server', database 'master'" that Azure SQL
/// Database returns for a server-scoped DMV into something that answers the question it provokes,
/// which is always the same one (#1631): "does this really need master?"
///
/// <para>It does not. The permission is not grantable at the server on Azure SQL Database at all —
/// the server is a logical concept there — so the message's mention of <c>master</c> reads as a
/// missing GRANT when it is really a statement about SERVICE OBJECTIVE. MS Learn documents
/// <c>VIEW DATABASE STATE</c> as sufficient for these DMVs on every Azure SQL Database service
/// objective EXCEPT Basic, S0, S1, and any database in an ELASTIC POOL; on those, only the server
/// admin, the Microsoft Entra admin, or a login in <c>##MS_ServerStateReader##</c> can read them.
/// A contained database user on a pooled database therefore cannot reach them however many database
/// grants it is given, and no amount of <c>master</c> access would change that either.</para>
///
/// <para>Appended to the stored error rather than replacing it: the raw SQL error number and text
/// stay intact for searching and for support, and the explanation rides along wherever the error is
/// already displayed — no new surface, no silent empty tab (#1591/#1679).</para>
/// </summary>
public static class AzureDmvPermissionHint
{
    /// <summary>
    /// The explanatory suffix for <paramref name="sqlErrorNumber"/>, or an empty string when none
    /// applies. Empty for every non-Azure target, so an on-prem 300 (a genuinely missing GRANT, which
    /// IS the fix there) keeps reading as one.
    /// </summary>
    public static string For(int sqlErrorNumber, bool isAzureSqlDb)
    {
        if (!isAzureSqlDb || sqlErrorNumber != 300)
        {
            return string.Empty;
        }

        return " — On Azure SQL Database this is a SERVICE OBJECTIVE limit, not a missing grant: "
            + "VIEW SERVER STATE / VIEW SERVER PERFORMANCE STATE cannot be granted at the server there. "
            + "VIEW DATABASE STATE covers this DMV on every service objective EXCEPT Basic, S0, S1, and "
            + "any database in an elastic pool, where reading it requires the server admin, the Entra "
            + "admin, or a login in the ##MS_ServerStateReader## server role. If this database is pooled "
            + "or Basic/S0/S1, add the monitoring login to ##MS_ServerStateReader## from the master "
            + "database; otherwise GRANT VIEW DATABASE STATE in this database. Other collectors are "
            + "unaffected and keep running.";
    }
}
