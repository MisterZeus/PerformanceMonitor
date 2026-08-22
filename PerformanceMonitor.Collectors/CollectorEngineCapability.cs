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
///
/// <para><b>Two axes, asked in order (#2530).</b> Engine KIND is asked first and engine EDITION second,
/// because a PostgreSQL target has no edition at all — <c>SERVERPROPERTY</c> does not exist there, the
/// connector stamps 0, and the edition axis therefore (correctly) declines to claim anything about it. Kind
/// is the coarser and more permanent fact of the two: an edition can change under an operator (a migration
/// to Azure SQL Database, an upgrade), while a target's DIALECT is what decides whether a collector's query
/// text could ever be sent at it. The kind axis is answered by the engine half of the collectors' own
/// dispatch gate (<see cref="CollectorCatalog.EngineMatches(ICollectorSchemaInfo, CollectorTargetInfo)"/>)
/// for exactly the reason the edition axis asks <see cref="CollectorCatalog.AppliesTo(ICollectorSchemaInfo,
/// CollectorTargetInfo)"/>: a hand-kept list of "what PostgreSQL does not have" would go stale in the
/// direction that keeps passing.</para>
/// </summary>
public static class CollectorEngineCapability
{
    /// <summary>The probe returned no edition — a server that has never connected, or a PostgreSQL target
    /// (<c>SERVERPROPERTY</c> does not exist there and the connector stamps 0). No capability claim is made
    /// for it on the EDITION axis: "we do not know" must never render as "this will never work". Since
    /// #2530 the store records engine KIND separately, so a PostgreSQL target IS distinguishable from an
    /// unconnected one — but that distinction is made there, not here, and this constant keeps meaning
    /// exactly what it meant.</summary>
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
    /// <para>Dropping a major from this list is not a cosmetic edit: a gate that only that major satisfies
    /// then matches no swept shape and is reported as a permanent engine gap.
    /// <c>AVersionGate_IsAnsweredAcrossTheRealMajors_NotByOneRepresentativeValue</c> asserts every value here
    /// is reachable on its own (#2518).</para>
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
    /// <para><b>Adding a field to <see cref="CollectorTargetInfo"/> that a SQL Server gate reads means adding
    /// it here too.</b> A fact this sweep never varies sits at its CLR default in every shape, so a gate
    /// written on it fails all of them and the derivation reports a permanent engine gap for a collector that
    /// runs — silently, because the gap set still looks plausible. That is not left to review:
    /// <c>EveryFactASqlServerGateReads_IsVariedBySweepOrFixedByEdition</c> decodes every SQL Server gate's IL
    /// for the <see cref="CollectorTargetInfo"/> getters it calls and fails if any of them names a fact this
    /// sweep leaves constant (#2518). Vary it here, or derive it from the engine edition the way the two
    /// Azure flags are — there is no third option and no list to add it to instead.</para>
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

    /// <summary>The same, one engine over — for the engine-KIND axis (#2530).</summary>
    private static readonly CollectorTargetInfo PostgresProbe = new() { Engine = CollectorTargetEngine.PostgreSql };

    /// <summary>The closing sentence both axes end on. One copy, because the two messages have to agree
    /// about what a permanent gap is; a second wording would eventually say something subtly different about
    /// whether it is worth chasing.</summary>
    private const string PermanentGapEpilogue =
        "This is a permanent engine capability gap, not a collection outage: checking collection health, " +
        "enabling a collector or starting a capture cannot change it.";

    /// <summary>
    /// True when SOME server of this engine edition runs <paramref name="collectorName"/> — i.e. the
    /// collector is not excluded by the engine alone.
    /// <para>Unknown (0) editions answer TRUE. So does an unknown collector name, matching
    /// <see cref="CollectorCatalog.AppliesTo(string, CollectorTargetInfo)"/>'s own true-on-miss default: a
    /// typo must not silently manufacture a permanent-gap claim. The test that scans the reads' collector
    /// names against the catalog is what keeps that default from hiding one.</para>
    /// </summary>
    public static bool IsCollectedOnEngineEdition(string collectorName, int engineEdition)
    {
        var definition = CollectorCatalog.Find(collectorName);

        /* True-on-miss, and it is the LOOKUP that owns that rule rather than the sweep: an unknown name has
           no gate to ask, so there is nothing to derive an answer from and the honest answer is "no claim". */
        return definition is null || IsCollectedOnEngineEdition(definition, engineEdition);
    }

    /// <summary>
    /// The same question asked of a DEFINITION rather than a catalog name. The by-name overload above is
    /// exactly this plus <see cref="CollectorCatalog.Find"/>'s true-on-miss lookup, so the two cannot answer
    /// differently — there is one sweep, not two.
    ///
    /// <para><b>Why the pair exists (#2518).</b> By name, this function can only ever be handed a gate that
    /// SHIPS, and a shipped gate is fixed at test time. Every assertion anyone can write against the by-name
    /// form is therefore a statement about today's collectors, and would pass just as well against a
    /// hard-coded set of gaps that happened to match them — which is precisely the failure the derivation
    /// exists to prevent. Taking a definition lets a caller hand the sweep a gate it CONTROLS and move it,
    /// so "the answer follows the gate" becomes something that can be demonstrated rather than believed.
    /// It is the same by-name/by-definition pair
    /// <see cref="CollectorCatalog.AppliesTo(ICollectorSchemaInfo, CollectorTargetInfo)"/> already carries,
    /// for the same reason: the definition is the thing that owns the answer, and the name is a way of
    /// finding one.</para>
    /// </summary>
    public static bool IsCollectedOnEngineEdition(ICollectorSchemaInfo definition, int engineEdition)
    {
        if (engineEdition == UnknownEngineEdition)
        {
            return true;
        }

        /* A PostgreSQL collector is not "missing" from a SQL Server engine edition — the question does not
           apply to it. Without this the sweep would report all eight PG definitions as permanent gaps on
           every SQL Server edition, because the dispatch gate it asks includes the engine half. */
        if (!CollectorCatalog.EngineMatches(definition, SqlServerProbe))
        {
            return true;
        }

        foreach (var target in TargetsWithEngineEdition(engineEdition))
        {
            if (CollectorCatalog.AppliesTo(definition, target))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="collectorName"/>'s query dialect can be sent at a server of this engine
    /// KIND — the second axis (#2530), and the one that separates a PostgreSQL target from a SQL Server that
    /// has never connected.
    /// <para>An absent, unrecognised, or unclassifiable kind answers TRUE, as does an unknown collector name
    /// — the same true-on-miss default the edition axis carries, for the same reason: nothing to ask means
    /// nothing to claim.</para>
    /// </summary>
    public static bool IsCollectedOnEngineKind(string collectorName, string? engineKind)
    {
        var definition = CollectorCatalog.Find(collectorName);

        return definition is null || IsCollectedOnEngineKind(definition, engineKind);
    }

    /// <summary>
    /// The engine-KIND question asked of a DEFINITION rather than a catalog name — the pair exists for the
    /// reason the edition axis's does: by name this can only ever be handed a gate that ships, so nothing
    /// asserted through it could distinguish a derivation from a list that happens to match.
    ///
    /// <para><b>Why only the ENGINE half of the dispatch gate, and not a sweep.</b> The edition axis sweeps
    /// every target shape an edition permits and claims a gap only when NO shape runs, because the facts it
    /// sweeps — msdb access, RDS, version — are fixable and a permanence claim over them would be the
    /// over-claim #2511 removed. On this axis there is nothing to sweep: a target's dialect is not a fact an
    /// operator can move, so <see cref="CollectorCatalog.EngineMatches(ICollectorSchemaInfo,
    /// CollectorTargetInfo)"/> — the collectors' own engine gate, which is what actually stops the dispatch
    /// — is the whole answer. It is still DERIVED: flip a definition's
    /// <see cref="ICollectorSchemaInfo.TargetEngine"/> and this answer flips with it, which is what
    /// <c>CollectorEngineCapabilityMovingGateTests</c> demonstrates rather than assumes.</para>
    ///
    /// <para><b>What this deliberately does NOT claim.</b> The PostgreSQL collectors' own
    /// <see cref="ICollectorDefinition{TRow}.AppliesTo"/> gates — Aurora-only surfaces, the PG16 floor on
    /// <c>pg_stat_io</c>, the writer-only autovacuum read — are the fixable/variable half on this side of the
    /// fence, and are left to the <c>unavailable</c> vocabulary exactly as msdb access is on the SQL Server
    /// side. Making <c>pg_wait_stats</c> a permanent gap on STOCK PostgreSQL is a real and available claim
    /// (Aurora-ness is fixed by the kind the way the Azure flags are fixed by the edition), and it wants the
    /// PostgreSQL twin of the #2518 sweep-dimension guard before it ships — filed rather than smuggled in
    /// here, because a sweep without that guard over-claims silently, which is the failure this whole
    /// mechanism exists to avoid.</para>
    /// </summary>
    public static bool IsCollectedOnEngineKind(ICollectorSchemaInfo definition, string? engineKind)
    {
        var probe = MonitoredEngineKind.EngineOf(engineKind) switch
        {
            CollectorTargetEngine.PostgreSql => PostgresProbe,
            CollectorTargetEngine.SqlServer => SqlServerProbe,
            /* Not known to be anything — the pre-#2530 state of every row, and of every store an older
               service wrote. No claim, which is the same silence UnknownEngineEdition keeps. */
            _ => null,
        };

        return probe is null || CollectorCatalog.EngineMatches(definition, probe);
    }

    /// <summary>
    /// The <c>not_collected</c> explanation for a read whose collector cannot run on this server's engine, or
    /// <c>null</c> when the engine DOES support it (in which case the read keeps its own miss vocabulary —
    /// <c>empty</c> for a genuine all-clear, <c>unavailable</c> for a gap worth chasing).
    ///
    /// <para>Returning the decision and the words as ONE value is deliberate: a caller that asked "is it
    /// gated?" and then separately built a message could answer one question and print the other.</para>
    ///
    /// <para><b>KIND before EDITION (#2530).</b> A PostgreSQL target's <paramref name="engineEdition"/> is 0,
    /// which the edition axis reads as "no claim" — correctly, since it genuinely knows nothing. Asking the
    /// kind axis first is what turns that silence into the true answer, and it must stay first: reversing the
    /// order would return null for every PostgreSQL target and the read would fall back to
    /// <c>unavailable</c>, which is exactly the wrong-cause message this closes.</para>
    ///
    /// <para><paramref name="engineKind"/> is <c>null</c> for a target whose kind the store does not record —
    /// a pre-#2530 row, a server that has not connected since the rung landed, or a SKU with no engine-kind
    /// column at all (Lite, which has no PostgreSQL target seam). Null makes NO claim on this axis; the
    /// edition axis then answers exactly as it did before.</para>
    /// </summary>
    public static string? NotCollectedMessage(string serverName, int engineEdition, string? engineKind, string collectorName)
    {
        var definition = CollectorCatalog.Find(collectorName);

        /* True-on-miss on the name, once, so neither axis below has to re-decide it. */
        if (definition is null)
        {
            return null;
        }

        if (!IsCollectedOnEngineKind(definition, engineKind))
        {
            /* "runs X" rather than "is an X target", because the descriptions are noun phrases and one of them
               starts with a vowel — an indefinite article in the template would read "a Aurora PostgreSQL".
               Article agreement belongs in the template or nowhere, never in a per-entry special case that
               the next entry gets wrong; the same reasoning the capture-path phrasing already follows. */
            return $"{serverName} runs {MonitoredEngineKind.DescribeEngineKind(engineKind)}. The " +
                   $"{collectorName} collector is written against " +
                   $"{DescribeTargetEngine(definition.TargetEngine)} and the dispatch gate's engine half never " +
                   $"sends it at another engine, so this server does not collect " +
                   $"{CapturePathOf(collectorName)}, and never will. {PermanentGapEpilogue}";
        }

        if (IsCollectedOnEngineEdition(definition, engineEdition))
        {
            return null;
        }

        /* "this server does not collect X" rather than "X is not collected", so the sentence reads correctly
           whether the capture path is singular ("the system_health extended-events ring buffer") or plural
           ("the Always On availability replica states"). Number agreement belongs in the template, not in a
           per-entry special case that the next entry would get wrong. */
        return $"{serverName} runs on {DescribeEngineEdition(engineEdition)} (EngineEdition {engineEdition}). " +
               $"The {collectorName} collector does not run on that engine — its own AppliesTo gate excludes it — " +
               $"so this server does not collect {CapturePathOf(collectorName)}, and never will. " +
               PermanentGapEpilogue;
    }

    /// <summary>What a gated-off collector would have captured. No entry is a vaguer sentence, never a wrong
    /// one — whichever axis called this has already decided that a gap exists.</summary>
    private static string CapturePathOf(string collectorName) =>
        CapturePathByCollector.TryGetValue(collectorName, out var described)
            ? described
            : "the data this read is served from";

    /// <summary>The engine a definition's query DIALECT targets, in words. Deliberately separate from
    /// <see cref="MonitoredEngineKind.DescribeEngineKind"/>: that describes a SERVER, which may be Aurora,
    /// and no collector is written against Aurora as opposed to PostgreSQL.</summary>
    private static string DescribeTargetEngine(CollectorTargetEngine engine) => engine switch
    {
        CollectorTargetEngine.PostgreSql => "PostgreSQL",
        _ => "SQL Server",
    };
}
