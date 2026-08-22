/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;

namespace PerformanceMonitor.Collectors;

/// <summary>
/// Whether a collector can EVER run against a server of a given SQL Server engine edition — and the one
/// sentence both SKUs return to a caller whose read is served by a collector that cannot (#2511).
///
/// <para><b>The defect this closes.</b> Twelve collectors gate themselves off on Azure SQL Database, and the
/// reads they feed did not know. On a live Azure SQL Database (<c>EngineEdition</c> 5, General Purpose and
/// Hyperscale alike) <c>sys.dm_xe_sessions</c> does not exist, so the <c>system_health</c> ring buffer can
/// never be read there; <see cref="SystemHealthEventsCollector"/> gates off correctly, and all nine
/// health-parser reads then told the operator to "check that collection is running for this server and that
/// its system_health session is started". Collection WAS running, and the session cannot be started on that
/// engine. A confident, specific, wrong instruction is worse than silence.</para>
///
/// <para><b>The answer is derived, never transcribed.</b> The capability question is answered by asking the
/// collector's OWN gate — <see cref="CollectorCatalog.AppliesTo(string, CollectorTargetInfo)"/>, the same
/// surface both runners dispatch through — over every target shape an engine edition permits. A hand-kept
/// list of "collectors Azure SQL DB does not have" would go stale in exactly the direction that makes it
/// pass: a gate that opened up would leave the list claiming a permanent gap that no longer exists, and
/// nothing would say so. Here, opening a gate silently stops the claim, which is the correct direction to
/// fail in.</para>
///
/// <para><b>Why "every target shape" rather than one representative target.</b> Engine edition is the only
/// fact the store holds for certain (<c>servers.sql_engine_edition</c>, stamped by the registration upsert on
/// every connect). Version, msdb access and RDS-ness are separate facts, and two of them are FIXABLE — an
/// operator can grant msdb access, and an upgrade moves the version floor. So the claim made here is
/// deliberately the narrow one: <i>there is no target with this engine edition, under any combination of the
/// other facts, for which this collector runs.</i> That is what makes "permanent" honest. A collector gated
/// off only for want of msdb access, or only below a version floor, is NOT reported as an engine gap — its
/// read keeps the <c>unavailable</c> vocabulary, which is what sends an operator to look, correctly.</para>
///
/// <para><b>The message lives here, not in the two MCP trees.</b> Both SKUs must answer this byte-identically,
/// and every shared sentence that lives twice has eventually been reworded once
/// (<c>McpMissMessageParityPinTests</c> exists because of it). One function called from both surfaces makes
/// parity structural rather than pinned.</para>
/// </summary>
public static class CollectorEngineCapability
{
    /// <summary>The probe returned no edition — a server that has never connected, or a PostgreSQL target
    /// (<c>SERVERPROPERTY</c> does not exist there and the connector stamps 0). No capability claim is made
    /// for it: "we do not know" must never render as "this will never work".</summary>
    public const int UnknownEngineEdition = 0;

    /// <summary><c>SERVERPROPERTY('EngineEdition')</c> for Azure SQL Database — the one edition that produces
    /// permanent gaps today, and the one the live probe in #2511 measured.</summary>
    public const int AzureSqlDatabaseEngineEdition = 5;

    /// <summary><c>SERVERPROPERTY('EngineEdition')</c> for Azure SQL Managed Instance.</summary>
    public const int AzureManagedInstanceEngineEdition = 8;

    /// <summary>
    /// The SQL major versions swept when asking whether ANY target of an engine edition runs a collector.
    /// <para>0 is "unknown, assume newest" (the value every version gate in the library already treats as a
    /// pass) and 99 is a version above any floor. The real majors in between are carried anyway so a future
    /// gate written as a RANGE — supported on 15 and 16 but not 17 — is answered correctly rather than by
    /// whichever single representative value happened to be chosen here.</para>
    /// </summary>
    private static readonly int[] MajorVersionSweep = { 0, 11, 12, 13, 14, 15, 16, 17, 99 };

    /// <summary>
    /// What a gated-off collector would have captured, in a noun phrase that completes "…so X is not
    /// collected for this server". PROSE ONLY: nothing here decides whether a gap exists, so an entry that
    /// falls out of date makes a message vaguer, never wrong. A collector with no entry still gets a correct
    /// message through the fallback in <see cref="NotCollectedMessage"/>.
    /// <para>Public so the tests can hold it to the catalog: every key must be a real collector name, and
    /// every key must be a collector that is genuinely gated off SOMEWHERE, so an entry cannot outlive the
    /// gate that gave it a reason to exist.</para>
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> CapturePathByCollector =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["system_health_events"] = "the system_health extended-events ring buffer",
            ["server_config"] = "the sys.configurations instance settings",
            ["trace_flags"] = "the DBCC TRACESTATUS trace-flag list",
            ["default_trace_events"] = "the built-in default trace",
            ["cpu_scheduler_stats"] = "the sys.dm_os_schedulers scheduler snapshot",
            ["memory_pressure_events"] = "the RING_BUFFER_RESOURCE_MONITOR ring buffer",
            ["tempdb_stats"] = "the tempdb space-usage snapshot",
            ["running_jobs"] = "the SQL Agent running-job snapshot",
            ["job_history"] = "the SQL Agent job history",
            ["agent_status"] = "the SQL Agent service status",
            ["database_states"] = "the sys.databases state snapshot",
            ["ag_replica_states"] = "the Always On availability replica states",
            ["ag_database_replica_states"] = "the Always On per-database replica states",
        };

    /// <summary>
    /// Human-readable <c>SERVERPROPERTY('EngineEdition')</c> description. The single copy: Darling's
    /// connector delegates here rather than keeping a second switch, because two edition tables in one repo
    /// drift and the one that drifts is never the one being read.
    /// </summary>
    public static string DescribeEngineEdition(int engineEdition) => engineEdition switch
    {
        1 => "Personal/Desktop",
        2 => "Standard",
        3 => "Enterprise",
        4 => "Express",
        AzureSqlDatabaseEngineEdition => "Azure SQL Database",
        6 => "Azure Synapse Analytics",
        AzureManagedInstanceEngineEdition => "Azure SQL Managed Instance",
        9 => "Azure SQL Edge",
        11 => "Azure Synapse serverless SQL pool",
        _ => $"Unknown ({engineEdition})",
    };

    /// <summary>
    /// Every target shape an engine edition permits, for the exhaustive gate sweep. The two Azure flags are
    /// FIXED by the edition (they are what the probe derives them from); everything else varies, because
    /// none of it is implied by the edition and all of it can differ server to server.
    /// <para>Public so a test can assert the sweep actually spans the dimensions the gates read, rather than
    /// trusting that it does.</para>
    /// </summary>
    public static IEnumerable<CollectorTargetInfo> TargetsWithEngineEdition(int engineEdition)
    {
        foreach (var major in MajorVersionSweep)
        {
            foreach (var hasMsdbAccess in new[] { true, false })
            {
                foreach (var isAwsRds in new[] { false, true })
                {
                    yield return new CollectorTargetInfo
                    {
                        Engine = CollectorTargetEngine.SqlServer,
                        IsAzureSqlDb = engineEdition == AzureSqlDatabaseEngineEdition,
                        IsAzureManagedInstance = engineEdition == AzureManagedInstanceEngineEdition,
                        IsAwsRds = isAwsRds,
                        HasMsdbAccess = hasMsdbAccess,
                        SqlMajorVersion = major,
                    };
                }
            }
        }
    }

    /// <summary>A bare SQL Server target, for the engine half of the dispatch gate alone.</summary>
    private static readonly CollectorTargetInfo SqlServerProbe = new() { Engine = CollectorTargetEngine.SqlServer };

    /// <summary>
    /// True when SOME server of this engine edition runs <paramref name="collectorName"/> — i.e. the
    /// collector is not excluded by the engine alone.
    /// <para>Unknown (0) editions answer TRUE. So does an unknown collector name, following
    /// <see cref="CollectorCatalog.AppliesTo(string, CollectorTargetInfo)"/>'s own true-on-miss default: a
    /// typo must not silently manufacture a permanent-gap claim. The test that scans the reads' collector
    /// names against the catalog is what keeps that default from hiding one.</para>
    /// </summary>
    public static bool IsCollectedOnEngineEdition(string collectorName, int engineEdition)
    {
        if (engineEdition == UnknownEngineEdition)
        {
            return true;
        }

        /* A PostgreSQL collector is not "missing" from a SQL Server engine edition — the question does not
           apply to it. Without this the sweep would report all eight PG definitions as permanent gaps on
           every SQL Server edition, because the dispatch gate it asks includes the engine half. */
        if (!CollectorCatalog.EngineMatches(collectorName, SqlServerProbe))
        {
            return true;
        }

        foreach (var target in TargetsWithEngineEdition(engineEdition))
        {
            if (CollectorCatalog.AppliesTo(collectorName, target))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The <c>not_collected</c> explanation for a read whose collector cannot run on this server's engine, or
    /// <c>null</c> when the engine DOES support it (in which case the read keeps its own miss vocabulary —
    /// <c>empty</c> for a genuine all-clear, <c>unavailable</c> for a gap worth chasing).
    /// <para>Returning the decision and the words as ONE value is deliberate: a caller that asked "is it
    /// gated?" and then separately built a message could answer one question and print the other.</para>
    /// </summary>
    public static string? NotCollectedMessage(string serverName, int engineEdition, string collectorName)
    {
        if (IsCollectedOnEngineEdition(collectorName, engineEdition))
        {
            return null;
        }

        /* No entry is a vaguer sentence, never a wrong one — the gate above already decided. */
        var capturePath = CapturePathByCollector.TryGetValue(collectorName, out var described)
            ? described
            : "the data this read is served from";

        return $"{serverName} runs on {DescribeEngineEdition(engineEdition)} (EngineEdition {engineEdition}). " +
               $"The {collectorName} collector does not run on that engine — its own AppliesTo gate excludes it — " +
               $"so {capturePath} is not collected for this server and never will be. This is a permanent engine " +
               "capability gap, not a collection outage: checking collection health, enabling a collector or " +
               "starting a capture cannot change it.";
    }
}
