/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// The failure-semantics contract behind every converted teardown (#1896), pinned directly.
///
/// <para><b>Why this did not exist before and needs to now.</b> <see cref="LiveStoreCleanup"/> shipped for
/// #1794 and roughly twenty call sites came to depend on it, but nothing asserted the one property they
/// depend ON: that a cleanup failure stays quiet while the body's exception is in flight, and is loud when it
/// is not. #1896 converts sixteen more <c>finally</c> blocks onto it, which is a lot of tests whose failure
/// reporting is now decided by six lines nobody was watching. If the <c>when (!bodySucceeded)</c> filter were
/// ever dropped or inverted, every one of those tests would start reporting the wrong exception — and the
/// symptom is the very thing that is hard to notice, because a masked failure still fails. The suite would
/// stay red and simply say the wrong reason.</para>
///
/// <para>Ungated on purpose. Both cases are decided by the exception filter, not by anything a store does, so
/// making them depend on <c>DARLING_TEST_PG</c> would retire the coverage on every ordinary run for nothing.
/// <see cref="LiveStoreCleanup.RunAsync"/> cannot be reached without a store — it opens a connection before it
/// reaches the filter — so <see cref="LiveStoreCleanup.RunOwnedAsync"/> is what carries the rule here. They
/// are deliberately the same six lines; a third test pins that they cannot drift apart.</para>
/// </summary>
public sealed class LiveStoreCleanupTests
{
    private sealed class CleanupBoom : Exception
    {
        public CleanupBoom()
            : base("the teardown blew up")
        {
        }
    }

    private sealed class BodyBoom : Exception
    {
        public BodyBoom()
            : base("the assertion that actually failed")
        {
        }
    }

    /// <summary>
    /// The masking case: the body already failed, so a cleanup failure must not replace it.
    ///
    /// <para>This is #1794 in one assertion. The reported failure was <c>InvalidOperationException:
    /// Connection is not open</c> thrown from a teardown helper, standing in front of the assertion that
    /// really failed — and it was thrown BECAUSE the body's failure had destroyed the connection the teardown
    /// then used, so the two are not independent events. The cleanup that cannot run is the expected
    /// consequence of the failure being reported, which is exactly why it must not become the report.</para>
    /// </summary>
    [Fact]
    public async Task WhenTheBodyFailed_ACleanupFailureIsSwallowed_SoTheBodysExceptionSurvives()
    {
        var cleanupRan = false;

        /* The shape every converted site now has, with the body's failure simulated. */
        var reported = await Record.ExceptionAsync(async () =>
        {
            var bodySucceeded = false;
            try
            {
                throw new BodyBoom();
            }
            finally
            {
                await LiveStoreCleanup.RunOwnedAsync(bodySucceeded, () =>
                {
                    cleanupRan = true;
                    throw new CleanupBoom();
                });
            }
        });

        Assert.True(cleanupRan, "cleanup must still be ATTEMPTED on the failing path — silent is not skipped.");
        Assert.IsType<BodyBoom>(reported);
    }

    /// <summary>
    /// The other half, and the half that keeps the first one honest: when the body SUCCEEDED there is no
    /// exception to protect, so a cleanup that cannot do its job must say so. Without this, "swallow
    /// everything" would pass the test above and quietly restore the debris problem #1873 was filed for.
    /// </summary>
    [Fact]
    public async Task WhenTheBodySucceeded_ACleanupFailurePropagates()
    {
        var reported = await Record.ExceptionAsync(async () =>
        {
            var bodySucceeded = false;
            try
            {
                bodySucceeded = true;
            }
            finally
            {
                await LiveStoreCleanup.RunOwnedAsync(bodySucceeded, () => throw new CleanupBoom());
            }
        });

        Assert.IsType<CleanupBoom>(reported);
    }

    /// <summary>A cleanup that succeeds reports nothing, on either path.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ASuccessfulCleanup_IsNeverItselfAFailure(bool bodySucceeded)
    {
        var ran = false;

        await LiveStoreCleanup.RunOwnedAsync(bodySucceeded, () =>
        {
            ran = true;
            return Task.CompletedTask;
        });

        Assert.True(ran);
    }

    /// <summary>
    /// The two overloads must keep the SAME rule.
    ///
    /// <para><see cref="LiveStoreCleanup.RunAsync"/> cannot be exercised without a store, so the behavioural
    /// tests above can only reach <see cref="LiveStoreCleanup.RunOwnedAsync"/>. That leaves a real hole: the
    /// connection-opening overload is the one sixteen conversions actually call, and it could have its filter
    /// changed with every test here still green. Reading the source is the check that closes it — the same
    /// technique <c>DarlingStoreUpgradeTests</c> uses to pin catch-ordering it cannot execute. It asserts the
    /// filter appears TWICE, so removing it from either overload fails.</para>
    /// </summary>
    [Fact]
    public void BothOverloads_CarryTheBodySucceededFilter()
    {
        var directory = FindTestProjectDirectory();
        Assert.True(directory is not null,
            "could not locate Darling/Darling.Tests by walking up from the test output directory.");

        var path = Path.Combine(directory!, "LiveStoreCleanup.cs");
        Assert.True(File.Exists(path), $"{path} does not exist — did LiveStoreCleanup.cs move or get renamed?");

        var source = File.ReadAllText(path);

        /* Guard the guard: if the file were restructured past recognition, an assertion on an absent string
           would pass vacuously, so pin the anchor it keys off first. */
        Assert.Contains("RunOwnedAsync", source, StringComparison.Ordinal);

        var occurrences = 0;
        var index = 0;
        while ((index = source.IndexOf("catch when (!bodySucceeded)", index, StringComparison.Ordinal)) >= 0)
        {
            occurrences++;
            index += 1;
        }

        Assert.True(occurrences == 2,
            "LiveStoreCleanup must carry `catch when (!bodySucceeded)` in BOTH overloads — RunAsync for cleanup "
            + "that moves to a fresh connection, RunOwnedAsync for cleanup that cannot. Found "
            + occurrences.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ". An unfiltered catch silently swallows cleanup failures on the passing path (the #1873 debris "
            + "problem); no catch at all lets a teardown replace the body's exception (the #1794 masking one).");
    }

    /// <summary>
    /// Walks up from the test output directory to the repo root (the directory holding
    /// <c>PerformanceMonitor.sln</c>) and returns this project's source directory. Same walk-up idiom as
    /// <c>LivePostgresCollectionHygieneTests.FindTestProjectDirectory</c>.
    /// </summary>
    private static string? FindTestProjectDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && directory is not null; i++)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PerformanceMonitor.sln")))
            {
                var source = Path.Combine(directory.FullName, "Darling", "Darling.Tests");
                return Directory.Exists(source) ? source : null;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
