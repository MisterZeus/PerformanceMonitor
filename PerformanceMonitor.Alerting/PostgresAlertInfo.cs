/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;

namespace PerformanceMonitor.Alerting;

/// <summary>
/// Freeze headroom for one database. <paramref name="AutovacuumFreezeMaxAge"/> travels with the row
/// because the threshold that matters is the SERVER's own setting, not a constant: a cluster tuned to
/// 1.5 billion is in a very different place at an age of 400 million than a stock one at 200 million.
/// </summary>
/// <param name="DatabaseName">The database this headroom belongs to.</param>
/// <param name="XidAge">Age of the oldest unfrozen XID (datfrozenxid).</param>
/// <param name="MultiXactAge">Age of the oldest unfrozen MultiXact (datminmxid).</param>
/// <param name="AutovacuumFreezeMaxAge">The server's autovacuum_freeze_max_age — the age at which
/// autovacuum force-starts a wraparound-prevention vacuum whether or not the table is otherwise due.</param>
public sealed record PostgresWraparoundAlertInfo(
    string DatabaseName,
    long XidAge,
    long MultiXactAge,
    long AutovacuumFreezeMaxAge)
{
    /// <summary>The worse of the two ages — either one reaching the wall stops writes.</summary>
    public long WorstAge => Math.Max(XidAge, MultiXactAge);

    /// <summary>Which counter is the worse one, for a message that names the right remedy.</summary>
    public string WorstCounter => MultiXactAge > XidAge ? "MultiXact" : "XID";
}

/// <summary>
/// The current xmin-horizon holder, with how persistent it has been.
/// </summary>
/// <param name="Source">Which of the four causes wins — session, replication_slot,
/// replication_slot_catalog, standby_feedback or prepared_transaction. They are indistinguishable by
/// symptom and need completely different fixes, so the alert must name it.</param>
/// <param name="Identifier">The specific holder (pid, slot name, gid, replica).</param>
/// <param name="XminAge">How far behind the horizon this holder is holding, in transactions.</param>
/// <param name="ObservationsHeld">How many collections in the window showed this source winning — the
/// chronic-versus-transient discriminator.</param>
/// <param name="ObservationsTotal">Collections in the window, so a caller can read the ratio.</param>
/// <param name="Detail">Free-text state the collector captured (e.g. "state=idle in transaction").</param>
public sealed record PostgresXminHorizonAlertInfo(
    string Source,
    string? Identifier,
    long XminAge,
    int ObservationsHeld,
    int ObservationsTotal,
    string? Detail);

/// <summary>
/// One replication slot's retention risk.
/// </summary>
/// <param name="SlotName">The slot.</param>
/// <param name="WalStatus">reserved / extended / unreserved / lost — the single most diagnostic column.</param>
/// <param name="IsActive">Whether anything is currently consuming it.</param>
/// <param name="RetainedWalBytes">WAL held because of this slot.</param>
/// <param name="RetainedWalGrowthBytes">Change across the window. Growth is what turns a large figure into
/// an emergency; a flat figure is a consumer that is behind but keeping pace.</param>
/// <param name="InactiveSince">When it went quiet, when the server reports it (PG17+).</param>
public sealed record PostgresSlotAlertInfo(
    string SlotName,
    string? WalStatus,
    bool IsActive,
    long RetainedWalBytes,
    long RetainedWalGrowthBytes,
    DateTime? InactiveSince);
