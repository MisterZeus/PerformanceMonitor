using Installer.Core;

namespace Installer.Tests;

/// <summary>
/// Pins the one thing a cancelled clean install must never do: come back looking like a completed run.
///
/// CleanInstallAsync drops the three Agent jobs, both Extended Events sessions, and then the DATABASE
/// (SET SINGLE_USER WITH ROLLBACK IMMEDIATE, DROP DATABASE) -- all of it BEFORE a single install file
/// runs. Cancelling in that window is the most destructive and least recoverable moment in the whole
/// installer, and it was the one window with no cancellation check in front of it.
///
/// A cancelled SqlCommand faults with SqlException, not OperationCanceledException, so the clean-install
/// catch swallowed the cancel as an ordinary "failure" and RETURNED NORMALLY. The Dashboard's cancel path
/// never ran. The user was told "Installation completed with 1 error(s)" over a database that may already
/// have been dropped; the dialog restored the verdict it had deliberately discarded for safety; and
/// because Save skips the connection test once that verdict is stamped, the server could then be saved
/// without ever being reconnected to.
///
/// These need no database precisely because the guard must fire BEFORE anything is contacted -- the
/// connection string points at a host that must never be reached. If the guard regresses, the test stops
/// seeing OperationCanceledException and starts seeing a SQL error or a timeout, which is exactly the
/// failure it exists to catch.
/// </summary>
public class CleanInstallCancellationTests
{
    /* Deliberately unreachable. Contacting it at all is the bug. */
    private const string UnreachableServer =
        "Server=this-host-must-never-be-contacted.invalid;Database=master;" +
        "User Id=nobody;Password=nobody;Connect Timeout=1;TrustServerCertificate=true";

    [Fact]
    public async Task CancelledBeforeStart_CleanInstall_Cancels_AndNeverContactsTheServer()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            InstallationService.ExecuteInstallationAsync(
                UnreachableServer,
                ScriptProvider.FromEmbeddedResources(),
                cleanInstall: true,
                cancellationToken: cts.Token));
    }

    [Fact]
    public async Task CancelledBeforeStart_OrdinaryInstall_CancelsToo()
    {
        /* The guard sits ahead of the clean-install branch, so it covers both shapes of run. */
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            InstallationService.ExecuteInstallationAsync(
                UnreachableServer,
                ScriptProvider.FromEmbeddedResources(),
                cleanInstall: false,
                cancellationToken: cts.Token));
    }

    /*
    COVERAGE BOUNDARY, stated honestly. These pin the PRE-file windows -- a token already cancelled trips
    the guard at the top of ExecuteInstallationAsync (and the loop-top guard) before any command runs, so
    they never exercise a cancel that lands INSIDE a running SqlCommand.

    That third window -- a cancel mid-file, which faults as SqlException rather than OperationCanceledException
    and used to be miscounted as a file failure -- is handled by a dedicated `catch when
    (cancellationToken.IsCancellationRequested)` that mirrors the clean-install branch. It can only be
    exercised end to end against a live server (a connection that opens, then a cancel during execution),
    which these DB-free tests must not do. It is covered by click-through item: cancel a normal install while
    a file is executing and confirm the dialog shows "cancelled", not "completed with N error(s)".
    */
}
