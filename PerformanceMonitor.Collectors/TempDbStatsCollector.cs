/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace PerformanceMonitor.Collectors;

/// <summary>
/// TempDB space usage from tempdb.sys.dm_db_file_space_usage plus the top tempdb-consuming
/// session (two result sets → one row). Extracted verbatim from Lite's
/// RemoteCollectorService.TempDb.cs. Always yields exactly one row — zeros when the result
/// sets are empty — matching the original collector's behavior. Applies to every SQL Server
/// target, Azure SQL Database included; see <see cref="AppliesTo"/> for the measurement that
/// removed the gate that used to exclude it.
/// </summary>
public sealed class TempDbStatsCollector : CollectorDefinitionBase<TempDbStatsCollector.Row>
{
    public static TempDbStatsCollector Instance { get; } = new();

    private TempDbStatsCollector()
    {
    }

    public readonly record struct Row(
        decimal UserObjectReservedMb,
        decimal InternalObjectReservedMb,
        decimal VersionStoreReservedMb,
        decimal TotalReservedMb,
        decimal UnallocatedMb,
        long TotalSessions,
        int TopSessionId,
        decimal TopSessionMb);

    public override string Name => "tempdb_stats";

    public override string TargetTable => "tempdb_stats";

    public override string? WatermarkColumn => null;

    /// <summary>
    /// Applies to every SQL Server target, Azure SQL Database included.
    ///
    /// <para><b>The gate that used to be here, and why it is gone (#2512).</b> <c>!target.IsAzureSqlDb</c>
    /// excluded the whole Azure SQL Database tier, both General Purpose and Hyperscale, on the premise that
    /// the first result set's THREE-part <c>tempdb.sys.dm_db_file_space_usage</c> reference could not be
    /// served there — that the collector "could only ever fail" on the platform. That premise was
    /// checkable and it is false.</para>
    ///
    /// <para><b>What was measured</b> (2026-08-22, this collector's SQL verbatim, both result sets, over an
    /// Entra token). It binds and returns real data on both tiers. <c>GP_S_Gen5_2</c> (EngineEdition 5,
    /// 12.0.2000.8): user 5.44 MB, internal 1.81 MB, version store 0.00 MB, unallocated 54.19 MB, and one
    /// session over threshold. <c>HS_S_Gen5_2</c>: user 1.88 MB, unallocated 60.69 MB.
    /// <c>SELECT COUNT(*) FROM tempdb.sys.dm_db_file_space_usage</c> returns 4 on both. No Azure-specific
    /// query variant is needed, and the second result set (<c>sys.dm_db_session_space_usage</c>, two-part
    /// and in-database) works too — so the one row this collector promises is a WHOLE row on Azure SQL
    /// Database, which is the objection the old comment raised against recovering half of it.</para>
    ///
    /// <para><b>And the figures describe something, rather than merely returning.</b> That was the open
    /// question, so it was moved rather than reasoned about: allocating ~57 MB into a <c>#temp</c> table on
    /// <c>GP_S_Gen5_2</c> moved <c>user_mb</c> 1.88 → 59.75 and <c>unallocated_mb</c> 60.69 → 2.69,
    /// while <c>sys.dm_db_session_space_usage</c> attributed 59.25 MB to the session that did it. The
    /// counters track actual allocation to within the reserved-versus-allocated difference you would expect,
    /// and the session view attributes it to the right session. Not a constant, not a stub, and not another
    /// database's tempdb.</para>
    ///
    /// <para><b>Why this is worth MORE here than on a box.</b> The tempdb ceiling on Azure SQL Database is
    /// governed by the SERVICE TIER: you cannot add files and you cannot grow past the tier's cap. On a box
    /// "tempdb is filling" means "go look at the disk". Here it means "you are approaching a hard limit you
    /// cannot raise without changing service objective" — more actionable and more urgent, and
    /// completely invisible for as long as this was gated off.</para>
    ///
    /// <para><b>What <c>unallocated_mb</c> is, precisely — do not overstate it.</b> It is free space in
    /// the tempdb files AS CURRENTLY ALLOCATED. Azure SQL Database creates those files small (4 files,
    /// ~62 MB total on both 2-vCore tiers measured) and autogrows them toward the tier cap, so on this
    /// platform <c>total_reserved / (total_reserved + unallocated)</c> measures distance to the next
    /// autogrow, not distance to the tier ceiling. One ordinary temp table moves it a long way: the
    /// measurement above took that ratio from 3% to 96% with a single <c>#temp</c>. The absolute MB and the
    /// TREND are the signal on this tier; the ratio needs a size floor before it can safely carry the
    /// <c>tempdb Space</c> alert — filed as #2515 rather than retuning a threshold every existing
    /// on-prem target already depends on.</para>
    ///
    /// <para><b>Permissions, which is what the #2150 field report was actually about.</b> Error 262,
    /// "VIEW DATABASE PERFORMANCE STATE permission denied in database 'tempdb'", is a real outcome for a
    /// login that lacks the grant — tempdb permissions are not persistable on Azure SQL Database, and in
    /// an elastic pool the database is not the permission boundary. But that is a property of the LOGIN, not
    /// of the tier, so it cannot be decided by a gate: it would have to deny every properly-permissioned
    /// Azure SQL Database target to spare the one that is not. #2512 classifies 262 as PERMISSIONS in both
    /// SKUs instead, so a login that genuinely cannot read this degrades to a non-fatal skip carrying an
    /// explanatory message, rather than the 11x-consecutive ERROR that motivated the gate.</para>
    ///
    /// <para><b>Managed Instance is unchanged</b> — it was never gated, it has a real tempdb and full
    /// DMV access, and <c>CollectorGateSurfacePinTests</c> asserts it explicitly so this cannot be
    /// re-narrowed by accident later.</para>
    /// </summary>
    public override bool AppliesTo(CollectorTargetInfo target) => true;

    public override bool RunsPerDatabase(CollectorTargetInfo target) => false;

    public override CollectorQuery BuildQuery(CollectorContext context) => new(QueryText);

    private const string QueryText = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT /* PerformanceMonitorLite */
    user_object_reserved_mb = CONVERT(decimal(18,2), SUM(dsu.user_object_reserved_page_count) * 8 / 1024.0),
    internal_object_reserved_mb = CONVERT(decimal(18,2), SUM(dsu.internal_object_reserved_page_count) * 8 / 1024.0),
    version_store_reserved_mb = CONVERT(decimal(18,2), SUM(dsu.version_store_reserved_page_count) * 8 / 1024.0),
    total_reserved_mb = CONVERT(decimal(18,2), SUM(dsu.user_object_reserved_page_count + dsu.internal_object_reserved_page_count + dsu.version_store_reserved_page_count) * 8 / 1024.0),
    unallocated_mb = CONVERT(decimal(18,2), SUM(dsu.unallocated_extent_page_count) * 8 / 1024.0)
FROM tempdb.sys.dm_db_file_space_usage AS dsu
OPTION(RECOMPILE);

SELECT /* PerformanceMonitorLite */ TOP (1)
    session_id = ssu.session_id,
    tempdb_mb = CONVERT(decimal(18,2), (ssu.user_objects_alloc_page_count + ssu.internal_objects_alloc_page_count) * 8 / 1024.0),
    total_sessions = (SELECT COUNT_BIG(*) FROM sys.dm_db_session_space_usage WHERE user_objects_alloc_page_count + internal_objects_alloc_page_count > 0)
FROM sys.dm_db_session_space_usage AS ssu
ORDER BY (ssu.user_objects_alloc_page_count + ssu.internal_objects_alloc_page_count) DESC
OPTION(RECOMPILE);";

    public override IReadOnlyList<CollectorColumn> PayloadColumns { get; } = new[]
    {
        new CollectorColumn("user_object_reserved_mb", CollectorColumnType.Decimal, 18, 2),
        new CollectorColumn("internal_object_reserved_mb", CollectorColumnType.Decimal, 18, 2),
        new CollectorColumn("version_store_reserved_mb", CollectorColumnType.Decimal, 18, 2),
        new CollectorColumn("total_reserved_mb", CollectorColumnType.Decimal, 18, 2),
        new CollectorColumn("unallocated_mb", CollectorColumnType.Decimal, 18, 2),
        new CollectorColumn("total_sessions_using_tempdb", CollectorColumnType.BigInt),
        new CollectorColumn("top_session_id", CollectorColumnType.Integer),
        new CollectorColumn("top_session_tempdb_mb", CollectorColumnType.Decimal, 18, 2),
    };

    public override async ValueTask<List<Row>> ReadAsync(DbDataReader reader, CollectorContext context, CancellationToken cancellationToken)
    {
        decimal userObjMb = 0, internalObjMb = 0, versionStoreMb = 0, totalReservedMb = 0, unallocatedMb = 0;
        int topSessionId = 0;
        long totalSessions = 0;
        decimal topSessionMb = 0;

        if (await reader.ReadAsync(cancellationToken))
        {
            userObjMb = reader.IsDBNull(0) ? 0m : reader.GetDecimal(0);
            internalObjMb = reader.IsDBNull(1) ? 0m : reader.GetDecimal(1);
            versionStoreMb = reader.IsDBNull(2) ? 0m : reader.GetDecimal(2);
            totalReservedMb = reader.IsDBNull(3) ? 0m : reader.GetDecimal(3);
            unallocatedMb = reader.IsDBNull(4) ? 0m : reader.GetDecimal(4);
        }

        if (await reader.NextResultAsync(cancellationToken) && await reader.ReadAsync(cancellationToken))
        {
            topSessionId = reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetValue(0));
            topSessionMb = reader.IsDBNull(1) ? 0m : reader.GetDecimal(1);
            totalSessions = reader.IsDBNull(2) ? 0L : reader.GetInt64(2);
        }

        return new List<Row>
        {
            new(userObjMb, internalObjMb, versionStoreMb, totalReservedMb, unallocatedMb, totalSessions, topSessionId, topSessionMb),
        };
    }

    public override void WritePayload(Row row, ICollectorRowWriter writer, CollectorContext context)
    {
        writer
            .Value(row.UserObjectReservedMb)      /* user_object_reserved_mb DECIMAL */
            .Value(row.InternalObjectReservedMb)  /* internal_object_reserved_mb DECIMAL */
            .Value(row.VersionStoreReservedMb)    /* version_store_reserved_mb DECIMAL */
            .Value(row.TotalReservedMb)           /* total_reserved_mb DECIMAL */
            .Value(row.UnallocatedMb)             /* unallocated_mb DECIMAL */
            .Value(row.TotalSessions)             /* total_sessions_using_tempdb BIGINT */
            .Value(row.TopSessionId)              /* top_session_id INTEGER */
            .Value(row.TopSessionMb);             /* top_session_tempdb_mb DECIMAL */
    }
}
