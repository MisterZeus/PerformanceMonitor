/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using PerformanceMonitor.Darling.Storage;

namespace Darling.Tests;

/// <summary>
/// Enables TimescaleDB on a connection the caller does not own, so a failed <c>CREATE EXTENSION</c> cannot
/// take the caller's connection with it (#1922).
///
/// <para><b>The failure this exists for.</b> <c>CREATE EXTENSION IF NOT EXISTS timescaledb</c> does not merely
/// ERROR when the library is on disk but absent from <c>shared_preload_libraries</c> — it TERMINATES THE
/// BACKEND:</para>
/// <code>
/// extension script file "timescaledb--2.28.1.sql", near line 63
/// server closed the connection unexpectedly
/// </code>
/// <para><see cref="TimescaleSupport.TryEnableAsync"/> catches that and returns <c>false</c>, which is correct
/// and is its documented contract — but the connection it was handed is now dead. A caller that keeps using
/// it gets <c>InvalidOperationException: Connection is not open</c> from whatever it happens to call next,
/// naming a frame several calls downstream and the real cause nowhere. That is the #1794 masking signature,
/// in fixture SETUP rather than teardown, and it is how a fresh local rig presents: the bundled
/// <c>pg-runtime.zip</c> ships the extension files, and a hand-rolled <c>initdb</c> has no preload line.</para>
///
/// <para><b>Why a separate connection rather than reopening after the fact.</b> Same reasoning as
/// <see cref="LiveStoreCleanup"/>, which this deliberately mirrors: the safest connection to risk is one
/// nobody else is holding. Reopening the caller's would also work, but it puts the burden on every call site
/// to remember, and a rule every call site must remember is one a new call site will not. Extension creation
/// is committed DDL, so the caller's own connection sees the extension immediately afterwards — and it was
/// opened by a backend that already had the library preloaded, since <c>shared_preload_libraries</c> loads at
/// postmaster start.</para>
///
/// <para><b>The product does not need this</b>, verified rather than assumed: <c>DarlingWorker</c> opens a
/// DEDICATED connection for the block, and gates every subsequent call on the returned flag being true, so a
/// <c>false</c> return means nothing touches it before it is disposed. The exposure is test-side only, which
/// is why the fix is too.</para>
/// </summary>
internal static class LiveTimescaleProbe
{
    /// <summary>
    /// Whether TimescaleDB is available on the store at <paramref name="connectionString"/>, established on a
    /// connection opened and disposed here. The caller's connections are untouched whatever happens.
    /// </summary>
    public static async Task<bool> TryEnableAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        /* SET, never inherit — the same Npgsql pooling trap LiveStoreCleanup documents: a physical session
           opened before the store's first migration keeps the pre-ALTER search_path for its whole life, and
           the hypertable helpers downstream use unqualified names. */
        await using (var setPath = new NpgsqlCommand("SET search_path = " + PgSchemaGenerator.SearchPath, connection))
        {
            await setPath.ExecuteNonQueryAsync(cancellationToken);
        }

        return await TimescaleSupport.TryEnableAsync(connection, null, cancellationToken);
    }
}
