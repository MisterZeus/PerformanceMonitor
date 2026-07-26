/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Linq;

namespace PerformanceMonitor.Common;

/// <summary>
/// The Availability Group TOPOLOGY banding and card projection, shared by every surface that draws it (#991):
/// the Darling web dashboard, the Darling WPF viewer's AG tab, and Lite's AG tab. Pure — no store, no WPF — so
/// the rules live in exactly one place and each app supplies only its own read and its own brushes.
///
/// <para>This is the display twin of <see cref="AgAlertPolicy"/>: that decides what to ALERT on, this decides
/// what to SHOW. They are deliberately separate because they need different rows — an alert keys on a
/// (ag_name, replica) identity and drops rows that cannot be keyed, whereas a topology view must still render a
/// quorum-lost AG whose names read NULL, which is precisely when an operator most wants to look at it.</para>
///
/// <para><b>One card per (reporting server, AG), never merged.</b> Every monitored replica reports the whole
/// AG's replica set, so an AG with several monitored replicas yields several cards. Collapsing them would be
/// wrong, not tidy: the DMV populates <c>operational_state</c> / <c>recovery_health</c> for the LOCAL replica
/// only, <c>connected_state</c> is meaningful only from the primary, and a quorum-lost instance answers from
/// cached metadata. Observed live: the primary reports two replicas and names the primary while the secondary
/// reports ONE replica and no primary at all. Merging lets the blind view overwrite the complete one. Lite is
/// single-server so it renders exactly one card per AG, which is the same rule with one reporter.</para>
/// </summary>
public static class AgTopology
{
    /* ─────────────────────────── banding ─────────────────────────── */

    /// <summary>Bands a replica's <c>synchronization_health_desc</c> — the one health column the DMV populates
    /// for EVERY replica rather than only the local one, which is why it anchors a card's badge.</summary>
    public static HealthSeverity SynchronizationHealthSeverity(string? healthDesc) =>
        Normalize(healthDesc) switch
        {
            "HEALTHY" => HealthSeverity.Healthy,
            "PARTIALLY_HEALTHY" => HealthSeverity.Warning,
            "NOT_HEALTHY" => HealthSeverity.Critical,
            _ => HealthSeverity.Unknown,
        };

    /// <summary>Bands <c>connected_state_desc</c>. Meaningful from the primary; NULL elsewhere, which bands
    /// Unknown rather than reading as a problem.</summary>
    public static HealthSeverity ConnectedStateSeverity(string? connectedDesc) =>
        Normalize(connectedDesc) switch
        {
            "CONNECTED" => HealthSeverity.Healthy,
            "DISCONNECTED" => HealthSeverity.Critical,
            _ => HealthSeverity.Unknown,
        };

    /// <summary>Bands <c>operational_state_desc</c>, whose documented values are exactly PENDING_FAILOVER /
    /// PENDING / ONLINE / OFFLINE / FAILED / FAILED_NO_QUORUM / NULL. Reported for the LOCAL replica only, so a
    /// remote replica's NULL bands Unknown. <c>ONLINE_IN_PROGRESS</c> deliberately does NOT appear — it belongs
    /// to <see cref="RecoveryHealthSeverity"/>, and banding it here would be an arm that can never fire.</summary>
    public static HealthSeverity OperationalStateSeverity(string? operationalDesc) =>
        Normalize(operationalDesc) switch
        {
            "ONLINE" => HealthSeverity.Healthy,
            "PENDING" or "PENDING_FAILOVER" => HealthSeverity.Warning,
            "OFFLINE" or "FAILED" or "FAILED_NO_QUORUM" => HealthSeverity.Critical,
            _ => HealthSeverity.Unknown,
        };

    /// <summary>Bands <c>recovery_health_desc</c> — documented as exactly ONLINE_IN_PROGRESS / ONLINE / NULL.</summary>
    public static HealthSeverity RecoveryHealthSeverity(string? recoveryDesc) =>
        Normalize(recoveryDesc) switch
        {
            "ONLINE" => HealthSeverity.Healthy,
            "ONLINE_IN_PROGRESS" => HealthSeverity.Warning,
            _ => HealthSeverity.Unknown,
        };

    /// <summary>Bands <c>role_desc</c>. RESOLVING is itself a finding: the replica holds neither real role,
    /// which is what a replica looks like mid-failover or after losing quorum.</summary>
    public static HealthSeverity RoleSeverity(string? roleDesc) =>
        Normalize(roleDesc) switch
        {
            "PRIMARY" or "SECONDARY" => HealthSeverity.Healthy,
            "RESOLVING" => HealthSeverity.Warning,
            _ => HealthSeverity.Unknown,
        };

    /// <summary>
    /// Bands a database's synchronization state AGAINST ITS AVAILABILITY MODE — the distinction a flat
    /// state-to-color map necessarily gets wrong. SYNCHRONIZING is the steady, correct state for an
    /// ASYNCHRONOUS_COMMIT replica (async never reaches SYNCHRONIZED), so amber there would paint every healthy
    /// async secondary as a problem; on a SYNCHRONOUS_COMMIT replica the same text means it is NOT currently
    /// protecting a commit. Suspended data movement outranks the state text entirely, because
    /// <c>secondary_lag_seconds</c> reads 0 — not NULL — while suspended, so a suspended replica otherwise
    /// presents as perfectly caught up.
    /// </summary>
    public static HealthSeverity DatabaseSyncSeverity(string? stateDesc, string? availabilityModeDesc, bool? isSuspended)
    {
        if (isSuspended == true)
        {
            return HealthSeverity.Critical;
        }

        return Normalize(stateDesc) switch
        {
            "SYNCHRONIZED" => HealthSeverity.Healthy,
            "SYNCHRONIZING" => string.Equals(Normalize(availabilityModeDesc), "ASYNCHRONOUS_COMMIT", StringComparison.Ordinal)
                ? HealthSeverity.Healthy
                : HealthSeverity.Warning,
            "NOT_SYNCHRONIZING" => HealthSeverity.Critical,
            "REVERTING" or "INITIALIZING" => HealthSeverity.Warning,
            _ => HealthSeverity.Unknown,
        };
    }

    /// <summary>Minutes to drain a queue at the current rate — DERIVED (queue / rate), because the DMV exposes
    /// only the queue (KB) and the rate (KB/s). An empty queue is 0 regardless of rate; a non-empty queue moving
    /// at 0 KB/s has no finite estimate and returns null, never a divide-by-zero infinity and never a
    /// misleading 0. Both inputs are instantaneous gauges, so this is a snapshot estimate, not a forecast.</summary>
    public static double? DrainMinutes(long? queueKb, long? rateKbPerSec)
    {
        if (queueKb is null)
        {
            return null;
        }

        if (queueKb.Value <= 0)
        {
            return 0;
        }

        if (rateKbPerSec is null || rateKbPerSec.Value <= 0)
        {
            return null;
        }

        return Math.Round(queueKb.Value / (double)rateKbPerSec.Value / 60.0, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>The human label for a card's badge — the same vocabulary the fleet bands use.</summary>
    public static string SeverityLabel(HealthSeverity severity) => severity switch
    {
        HealthSeverity.Healthy => "Healthy",
        HealthSeverity.Warning => "Warning",
        HealthSeverity.Critical => "Critical",
        _ => "Unknown",
    };

    /// <summary>Upper-cases and collapses the DMVs' two spellings to one form: the database grain reports states
    /// space-separated (<c>NOT SYNCHRONIZING</c>) where the replica grain uses underscores (<c>NOT_HEALTHY</c>),
    /// and the collectors store each verbatim.</summary>
    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "" : value.Trim().Replace(' ', '_').ToUpperInvariant();

    /* ─────────────────────────── projection ─────────────────────────── */

    /// <summary>
    /// Shapes raw rows into cards. Pure, so the grouping and every band unit-test without a store — which is how
    /// both apps' test matrices pin the identical behavior.
    ///
    /// <para>The REPLICA grain defines the card set: a database row whose (server, AG) has no replica row is
    /// dropped, reachable only when the two collectors' newest snapshots straddle an AG being created or
    /// dropped. A database grid under a header with no replicas to explain it would be less legible than waiting
    /// one sweep for the grains to agree.</para>
    /// </summary>
    public static List<AgTopologyCard> BuildCards(
        IReadOnlyList<AgTopologyReplicaRow> replicas,
        IReadOnlyList<AgTopologyDatabaseRow> databases)
    {
        ArgumentNullException.ThrowIfNull(replicas);
        ArgumentNullException.ThrowIfNull(databases);

        var databasesByGroup = databases
            .GroupBy(d => (d.ServerId, Key(d.AgName)))
            .ToDictionary(g => g.Key, g => g.ToList());

        var cards = new List<AgTopologyCard>();

        foreach (var group in replicas.GroupBy(r => (r.ServerId, Key(r.AgName))))
        {
            var rows = group.ToList();
            var first = rows[0];
            databasesByGroup.TryGetValue(group.Key, out var dbRows);

            var replicaItems = rows.ConvertAll(ToReplica);
            var databaseItems = (dbRows ?? new List<AgTopologyDatabaseRow>()).ConvertAll(ToDatabase);

            /* Worst band anywhere in the card. Unknown is the LOWEST enum value, so the NULLs a non-local or
               quorum-lost replica reports never mask a real Healthy/Warning/Critical. */
            var worst = HealthSeverity.Unknown;
            foreach (var replica in replicaItems)
            {
                worst = Worse(worst, replica.Severity);
            }

            foreach (var database in databaseItems)
            {
                worst = Worse(worst, database.SynchronizationStateSeverity);
            }

            cards.Add(new AgTopologyCard
            {
                ServerId = first.ServerId,
                ServerName = first.ServerName,
                AgName = first.AgName,
                CollectionTime = first.CollectionTime,
                DatabaseCollectionTime = dbRows is { Count: > 0 } ? dbRows[0].CollectionTime : null,
                PrimaryReplica = replicaItems.FirstOrDefault(r => r.IsPrimary)?.ReplicaServerName,
                Severity = worst,
                Replicas = replicaItems,
                Databases = databaseItems,
            });
        }

        /* Worst-first, then AG name, then reporting server — problems surface without scrolling, and the several
           perspectives on one AG stay adjacent once severity ties. */
        cards.Sort(static (a, b) =>
        {
            var bySeverity = b.Severity.CompareTo(a.Severity);
            if (bySeverity != 0)
            {
                return bySeverity;
            }

            var byName = string.Compare(a.AgName ?? "", b.AgName ?? "", StringComparison.OrdinalIgnoreCase);
            return byName != 0 ? byName : string.Compare(a.ServerName, b.ServerName, StringComparison.OrdinalIgnoreCase);
        });

        return cards;
    }

    /// <summary>The header counts. Groups and views differ exactly when an AG has more than one monitored
    /// reporter, and that difference is what a reader seeing one AG name twice needs said out loud.</summary>
    public static (int DistinctGroups, int ReportingServers, int Views) Counts(IReadOnlyList<AgTopologyCard> cards)
    {
        ArgumentNullException.ThrowIfNull(cards);

        return (
            cards.Select(c => Key(c.AgName)).Distinct(StringComparer.Ordinal).Count(),
            cards.Select(c => c.ServerId).Distinct().Count(),
            cards.Count);
    }

    private static string Key(string? agName) => (agName ?? "").ToUpperInvariant();

    private static HealthSeverity Worse(HealthSeverity a, HealthSeverity b) => a > b ? a : b;

    private static AgTopologyReplica ToReplica(AgTopologyReplicaRow row)
    {
        var syncHealth = SynchronizationHealthSeverity(row.SynchronizationHealthDesc);
        var connected = ConnectedStateSeverity(row.ConnectedStateDesc);
        var operational = OperationalStateSeverity(row.OperationalStateDesc);
        var recovery = RecoveryHealthSeverity(row.RecoveryHealthDesc);
        var role = RoleSeverity(row.RoleDesc);

        return new AgTopologyReplica
        {
            ReplicaServerName = row.ReplicaServerName,
            RoleDesc = row.RoleDesc,
            IsPrimary = string.Equals(row.RoleDesc, "PRIMARY", StringComparison.OrdinalIgnoreCase),
            IsLocal = row.IsLocal,
            OperationalStateDesc = row.OperationalStateDesc,
            ConnectedStateDesc = row.ConnectedStateDesc,
            RecoveryHealthDesc = row.RecoveryHealthDesc,
            SynchronizationHealthDesc = row.SynchronizationHealthDesc,
            AvailabilityModeDesc = row.AvailabilityModeDesc,
            FailoverModeDesc = row.FailoverModeDesc,
            EndpointUrl = row.EndpointUrl,
            Severity = Worse(Worse(Worse(Worse(syncHealth, connected), operational), recovery), role),
        };
    }

    private static AgTopologyDatabase ToDatabase(AgTopologyDatabaseRow row) => new()
    {
        DatabaseName = row.DatabaseName,
        ReplicaServerName = row.ReplicaServerName,
        IsLocal = row.IsLocal,
        SynchronizationStateDesc = row.SynchronizationStateDesc,
        SynchronizationStateSeverity = DatabaseSyncSeverity(row.SynchronizationStateDesc, row.AvailabilityModeDesc, row.IsSuspended),
        AvailabilityModeDesc = row.AvailabilityModeDesc,
        LogSendQueueKb = row.LogSendQueueKb,
        RedoQueueKb = row.RedoQueueKb,
        LogSendRateKbPerSec = row.LogSendRateKbPerSec,
        RedoRateKbPerSec = row.RedoRateKbPerSec,
        EstimatedSendDrainMinutes = DrainMinutes(row.LogSendQueueKb, row.LogSendRateKbPerSec),
        EstimatedRedoCompletionMinutes = DrainMinutes(row.RedoQueueKb, row.RedoRateKbPerSec),
        SecondaryLagSeconds = row.SecondaryLagSeconds,
        IsSuspended = row.IsSuspended,
        SuspendReasonDesc = row.SuspendReasonDesc,
    };
}

/// <summary>One replica-grain row as collected, app-neutral. <c>ServerName</c> is the REPORTING server.</summary>
public sealed class AgTopologyReplicaRow
{
    public int ServerId { get; init; }
    public string ServerName { get; init; } = "";
    public DateTime CollectionTime { get; init; }
    public string? AgName { get; init; }
    public string? ReplicaServerName { get; init; }
    public string? RoleDesc { get; init; }
    public bool? IsLocal { get; init; }
    public string? OperationalStateDesc { get; init; }
    public string? ConnectedStateDesc { get; init; }
    public string? RecoveryHealthDesc { get; init; }
    public string? SynchronizationHealthDesc { get; init; }
    public string? AvailabilityModeDesc { get; init; }
    public string? FailoverModeDesc { get; init; }
    public string? EndpointUrl { get; init; }
}

/// <summary>One database-grain row as collected. Queue sizes are KB and rates KB/s (the DMV's units), both
/// instantaneous gauges rather than counters.</summary>
public sealed class AgTopologyDatabaseRow
{
    public int ServerId { get; init; }
    public string ServerName { get; init; } = "";
    public DateTime CollectionTime { get; init; }
    public string? AgName { get; init; }
    public string? DatabaseName { get; init; }
    public string? ReplicaServerName { get; init; }
    public bool? IsLocal { get; init; }
    public string? SynchronizationStateDesc { get; init; }
    public long? LogSendQueueKb { get; init; }
    public long? RedoQueueKb { get; init; }
    public long? LogSendRateKbPerSec { get; init; }
    public long? RedoRateKbPerSec { get; init; }
    public bool? IsSuspended { get; init; }
    public string? SuspendReasonDesc { get; init; }
    public string? AvailabilityModeDesc { get; init; }
    public long? SecondaryLagSeconds { get; init; }
}

/// <summary>One replica chip, pre-banded. The view layer adds only a brush for <see cref="Severity"/>.</summary>
public sealed class AgTopologyReplica
{
    public string? ReplicaServerName { get; init; }
    public string? RoleDesc { get; init; }
    public bool IsPrimary { get; init; }
    public bool? IsLocal { get; init; }
    public string? OperationalStateDesc { get; init; }
    public string? ConnectedStateDesc { get; init; }
    public string? RecoveryHealthDesc { get; init; }
    public string? SynchronizationHealthDesc { get; init; }
    public string? AvailabilityModeDesc { get; init; }
    public string? FailoverModeDesc { get; init; }
    public string? EndpointUrl { get; init; }
    public HealthSeverity Severity { get; init; }

    public string RoleDisplay => string.IsNullOrWhiteSpace(RoleDesc) ? "UNKNOWN ROLE" : RoleDesc!;
    public string NameDisplay => string.IsNullOrWhiteSpace(ReplicaServerName) ? "—" : ReplicaServerName!;

    /// <summary>The states worth reading at chip size. A column the DMV leaves null is omitted rather than shown
    /// as an em dash the reader must interpret; recovery health joins only when it is NOT plain ONLINE, since a
    /// healthy one would merely repeat the operational state's "ONLINE".</summary>
    public string StateDisplay
    {
        get
        {
            var parts = new List<string>(4);
            foreach (var value in new[] { SynchronizationHealthDesc, ConnectedStateDesc, OperationalStateDesc })
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    parts.Add(value!);
                }
            }

            if (!string.IsNullOrWhiteSpace(RecoveryHealthDesc)
                && !string.Equals(RecoveryHealthDesc, "ONLINE", StringComparison.OrdinalIgnoreCase))
            {
                parts.Add("recovery " + RecoveryHealthDesc);
            }

            return string.Join(" · ", parts);
        }
    }

    public string ModeDisplay =>
        string.Join(" · ", new[] { AvailabilityModeDesc, FailoverModeDesc }.Where(v => !string.IsNullOrWhiteSpace(v)));
}

/// <summary>One database row in a card's grid, pre-banded.</summary>
public sealed class AgTopologyDatabase
{
    public string? DatabaseName { get; init; }
    public string? ReplicaServerName { get; init; }
    public bool? IsLocal { get; init; }
    public string? SynchronizationStateDesc { get; init; }
    public HealthSeverity SynchronizationStateSeverity { get; init; }
    public string? AvailabilityModeDesc { get; init; }
    public long? LogSendQueueKb { get; init; }
    public long? RedoQueueKb { get; init; }
    public long? LogSendRateKbPerSec { get; init; }
    public long? RedoRateKbPerSec { get; init; }
    public double? EstimatedSendDrainMinutes { get; init; }
    public double? EstimatedRedoCompletionMinutes { get; init; }
    public long? SecondaryLagSeconds { get; init; }
    public bool? IsSuspended { get; init; }
    public string? SuspendReasonDesc { get; init; }

    public string DatabaseDisplay => DatabaseName ?? "—";
    public string ReplicaDisplay => ReplicaServerName ?? "—";
    public string SyncStateDisplay => SynchronizationStateDesc ?? "—";
    public string SendQueueDisplay => LogSendQueueKb?.ToString("N0") ?? "—";
    public string RedoQueueDisplay => RedoQueueKb?.ToString("N0") ?? "—";
    public string SendRateDisplay => LogSendRateKbPerSec?.ToString("N0") ?? "—";
    public string RedoRateDisplay => RedoRateKbPerSec?.ToString("N0") ?? "—";
    public string SendDrainDisplay => EstimatedSendDrainMinutes?.ToString("N1") ?? "—";
    public string RedoDrainDisplay => EstimatedRedoCompletionMinutes?.ToString("N1") ?? "—";

    /// <summary>Lag needs its suspension context inline: the DMV reports 0 — not null — while data movement is
    /// suspended, so a suspended replica reads as perfectly caught up on the number alone.</summary>
    public string LagDisplay => SecondaryLagSeconds is null
        ? "—"
        : IsSuspended == true ? $"{SecondaryLagSeconds:N0} s (suspended)" : $"{SecondaryLagSeconds:N0} s";

    public string DataMovementDisplay => IsSuspended == true
        ? (string.IsNullOrWhiteSpace(SuspendReasonDesc) ? "Suspended" : "Suspended: " + SuspendReasonDesc)
        : IsSuspended == false ? "Moving" : "—";

    /// <summary>Only a suspended row earns the attention treatment; everything else is coloured by its sync
    /// state, which already carries the verdict.</summary>
    public HealthSeverity DataMovementSeverity =>
        IsSuspended == true ? HealthSeverity.Critical : HealthSeverity.Unknown;
}

/// <summary>One reporting server's VIEW of one Availability Group — the card each app renders.</summary>
public sealed class AgTopologyCard
{
    public int ServerId { get; init; }

    /// <summary>The REPORTING server — the monitored instance whose DMVs produced this view, not necessarily the
    /// AG's primary.</summary>
    public string ServerName { get; init; } = "";

    public string? AgName { get; init; }
    public DateTime CollectionTime { get; init; }
    public DateTime? DatabaseCollectionTime { get; init; }
    public string? PrimaryReplica { get; init; }
    public HealthSeverity Severity { get; init; }
    public IReadOnlyList<AgTopologyReplica> Replicas { get; init; } = Array.Empty<AgTopologyReplica>();
    public IReadOnlyList<AgTopologyDatabase> Databases { get; init; } = Array.Empty<AgTopologyDatabase>();

    public string AgNameDisplay => string.IsNullOrWhiteSpace(AgName) ? "—" : AgName!;
    public string SeverityLabel => AgTopology.SeverityLabel(Severity);
    public bool HasDatabases => Databases.Count > 0;

    public string PrimaryDisplay => string.IsNullOrWhiteSpace(PrimaryReplica)
        ? "No primary reported"
        : "Primary: " + PrimaryReplica;

    /// <summary>Whose view this is, and how fresh. The two collector sweeps land independently, so the database
    /// grain gets its own "as of" whenever it differs — surfaced rather than hidden, because the collectors write
    /// nothing for a server with no AGs, so a card that stops refreshing keeps its last instant.</summary>
    public string SubtitleDisplay
    {
        get
        {
            var text = $"As reported by {ServerName} · collected {CollectionTime:yyyy-MM-dd HH:mm} UTC";
            if (DatabaseCollectionTime.HasValue && DatabaseCollectionTime.Value != CollectionTime)
            {
                text += $" · databases {DatabaseCollectionTime.Value:HH:mm} UTC";
            }

            return text;
        }
    }
}
