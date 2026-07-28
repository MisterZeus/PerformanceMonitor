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
/// Shared-store cleanup that survives the failure it is cleaning up after (#1794).
///
/// <para>The live-Postgres classes' finallys used to run their restore/delete statements on the BODY's
/// connection. That connection is the one thing the failure being reported may have destroyed — a broken
/// socket, a taint from a failed COPY, a cancellation mid-command all leave it closed — and a cleanup on a
/// closed connection does three bad things at once: it throws
/// <c>InvalidOperationException: Connection is not open</c> from a helper frame (which is exactly the
/// signature #1794 chased), that throw REPLACES the body's in-flight exception (a finally-throw wins, so
/// the real failure is masked), and it abandons every cleanup statement after the first — leaving renames,
/// aggregate snapshots, and dim rows behind in the reused store, which is how one masked failure
/// manufactures the next run's "unrelated" flakes.</para>
///
/// <para>This helper gives cleanup its own freshly-opened connection and its own lifetime:
/// <list type="bullet">
/// <item><see cref="CancellationToken.None"/>, deliberately — a cancelled test run must still restore the
/// store, and the body's token is by definition already signalled on that path. Statement runtime is
/// bounded by the commands' own timeouts.</item>
/// <item>When the BODY already failed, a cleanup exception is swallowed: the body's exception is the
/// story, and replacing it with cleanup noise is the masking this exists to end. When the body
/// SUCCEEDED, cleanup failures propagate loudly — a cleanup that cannot do its job must not be
/// silent, or the debris problem returns by another door.</item>
/// </list></para>
///
/// <para>Usage shape (the <c>bodySucceeded</c> flag is the last statement of the try):</para>
/// <code>
/// var bodySucceeded = false;
/// try { ...; bodySucceeded = true; }
/// finally
/// {
///     await LiveStoreCleanup.RunAsync(connectionString!, bodySucceeded, async (cleanup, cleanupCt) =>
///     {
///         ...the old finally statements, against `cleanup`...
///     });
/// }
/// </code>
/// </summary>
internal static class LiveStoreCleanup
{
    public static async Task RunAsync(
        string connectionString,
        bool bodySucceeded,
        Func<NpgsqlConnection, CancellationToken, Task> cleanup)
    {
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(CancellationToken.None);

            /* SET, never inherit: Npgsql pools physical sessions, and one opened before the store's
               first migration keeps the pre-ALTER default "$user", public for its whole life — bare
               table names then resolve to nothing (42P01). The observer session in the atomic-visibility
               test documents the same trap; cleanup statements use unqualified names, so it applies
               here identically. */
            await using (var setPath = new NpgsqlCommand("SET search_path = " + PgSchemaGenerator.SearchPath, connection))
            {
                await setPath.ExecuteNonQueryAsync(CancellationToken.None);
            }

            await cleanup(connection, CancellationToken.None);
        }
        catch when (!bodySucceeded)
        {
            /* The body's exception is in flight; a throw from this finally would replace it. The store
               may still be dirty on this path (cleanup itself failed), but the reported failure is now
               the REAL one — and a dirty store with a true diagnosis beats a clean-looking one with
               "Connection is not open" standing in front of it. */
        }
    }
}
