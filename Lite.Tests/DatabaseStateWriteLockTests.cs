/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Threading.Tasks;
using PerformanceMonitorLite.Services;
using PerformanceMonitorLite.Tests;
using Xunit;

namespace Lite.Tests;

/// <summary>
/// Runs ALONE. These tests take the process-wide <c>s_dbLock</c> write lock and hold it across an awaited
/// call, and every DuckDB access in the assembly goes through that same static lock — so a neighbour running
/// concurrently would block on it and could burn its own 5-second budget. Serialising the collection is what
/// makes holding the lock safe to do at all.
/// </summary>
[CollectionDefinition("db-write-lock", DisableParallelization = true)]
public sealed class DatabaseStateWriteLockCollection { }

/// <summary>
/// #2208: the database-state write paths take the WRITE lock. They used the read lock, which let their
/// INSERT/UPDATE/DELETE interleave with archival and starved writers process-wide — a
/// <c>ReaderWriterLockSlim</c> writer waits for every reader to drain, and <c>OpenWriteConnectionAsync</c>
/// gives up after five seconds.
///
/// <para>WHAT THESE PIN, precisely: that the two OPERATOR paths acquire the write lock, observed by holding it
/// and requiring them to report the wait rather than proceed. That is the #2212 decision worth locking down —
/// those methods deliberately do NOT swallow the timeout, because they are button presses and the exception's
/// message ("try again in a few moments") is written to be shown, so silently dropping an operator's declared
/// expected state would be the worst outcome available.</para>
///
/// <para>WHAT THEY CANNOT PIN, and why it is a limitation rather than an omission: the deviation read's
/// best-effort skip path. Forcing its prologue to time out means holding the write lock, but the read that
/// follows takes the READ lock, and <c>AcquireReadLock</c> has no timeout — so the call would block forever
/// rather than demonstrating the skip. Testing that arm needs the timeout injected, which means changing
/// production shape for testability. Recorded here so the gap is visible rather than assumed covered.</para>
///
/// <para>Neither test is cleanly watched-red either: before #2212 these methods took the read lock, so with
/// the write lock held they would have blocked indefinitely on <c>EnterReadLock</c> instead of failing. The
/// pre-fix signal is a hang, not a red test. That is worth saying out loud — a test whose "before" state is a
/// hang proves less than one whose before state is an assertion failure.</para>
/// </summary>
[Collection("db-write-lock")]
public sealed class DatabaseStateWriteLockTests : IClassFixture<SharedDuckDbFixture>
{
    private readonly SharedDuckDbFixture _fixture;

    public DatabaseStateWriteLockTests(SharedDuckDbFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task SetDatabaseStateExpected_TakesTheWriteLock_AndReportsTheWaitInsteadOfProceeding()
    {
        var service = new LocalDataService(_fixture.DuckDb);

        using (_fixture.DuckDb.AcquireWriteLock())
        {
            /* An operator declaring an expected state while archival holds the lock must be TOLD, not ignored.
               The message this throws is what the overrides window already surfaces through its generic
               handler, so the wait reaches the person who pressed the button. */
            await Assert.ThrowsAsync<TimeoutException>(
                () => service.SetDatabaseStateExpectedAsync(1, "probedb", "OFFLINE"));
        }
    }

    [Fact]
    public async Task ResetDatabaseStateExpectedToCurrent_TakesTheWriteLock_AndReportsTheWait()
    {
        var service = new LocalDataService(_fixture.DuckDb);

        using (_fixture.DuckDb.AcquireWriteLock())
        {
            await Assert.ThrowsAsync<TimeoutException>(
                () => service.ResetDatabaseStateExpectedToCurrentAsync(1, "probedb"));
        }
    }
}
