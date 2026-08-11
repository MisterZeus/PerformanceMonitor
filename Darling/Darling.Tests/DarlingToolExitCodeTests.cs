/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PerformanceMonitor.Darling.Service;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// The bundled-Postgres tool exit-code decoder (#2186). The bug it exists for was reported twice from
/// the field: a managed bootstrap failed with <c>initdb failed (exit code -1073741515) ... Output:</c>
/// and nothing else — a signed Win32 integer plus an empty field, on the one class of failure where the
/// empty field is guaranteed rather than informative, because the process was killed in the LOADER
/// before it could write a line. The operator's attention then went to the follow-on missing-credential
/// message and darling.json, neither of which was the fault.
///
/// <para>These pin the decode itself. The pins that the SHIPPED messages carry it live in
/// <see cref="DarlingManagedPostgresTests"/> — a correct decoder nothing calls is exactly the shape of
/// the defect #1738 already was.</para>
/// </summary>
public sealed class DarlingToolExitCodeTests
{
    /* The four loader statuses, as .NET reports them from Process.ExitCode (signed). */
    private const int StatusDllNotFound = unchecked((int)0xC0000135);
    private const int StatusEntryPointNotFound = unchecked((int)0xC0000139);
    private const int StatusInvalidImageFormat = unchecked((int)0xC000007B);
    private const int StatusDllInitFailed = unchecked((int)0xC0000142);
    private const int StatusAccessDenied = unchecked((int)0xC0000022);
    private const int StatusAccessViolation = unchecked((int)0xC0000005);
    private const int StatusNoMemory = unchecked((int)0xC0000017);

    private const string InitDb = @"C:\PerformanceMonitorDarling\pg-runtime\pgsql\bin\initdb.exe";

    /// <summary>
    /// The literal number from the field report decodes. Written against the DECIMAL the operator
    /// actually saw rather than a hex constant, so this test fails if the two ever stop being the same
    /// number — that equality is the entire premise of the fix.
    /// </summary>
    [Fact]
    public void Describe_DecodesTheExactCodeTheFieldReported()
    {
        Assert.Equal(StatusDllNotFound, -1073741515);

        var described = DarlingToolExitCode.Describe(-1073741515);

        Assert.Contains("-1073741515", described, StringComparison.Ordinal);
        Assert.Contains("0xC0000135", described, StringComparison.Ordinal);
        Assert.Contains("STATUS_DLL_NOT_FOUND", described, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(StatusEntryPointNotFound, "0xC0000139", "STATUS_ENTRYPOINT_NOT_FOUND")]
    [InlineData(StatusInvalidImageFormat, "0xC000007B", "STATUS_INVALID_IMAGE_FORMAT")]
    [InlineData(StatusDllInitFailed, "0xC0000142", "STATUS_DLL_INIT_FAILED")]
    [InlineData(StatusAccessDenied, "0xC0000022", "STATUS_ACCESS_DENIED")]
    [InlineData(StatusAccessViolation, "0xC0000005", "STATUS_ACCESS_VIOLATION")]
    public void Describe_NamesTheOtherStatusesWorthNaming(int exitCode, string hex, string name)
    {
        var described = DarlingToolExitCode.Describe(exitCode);

        Assert.Contains(hex, described, StringComparison.Ordinal);
        Assert.Contains(name, described, StringComparison.Ordinal);
    }

    /// <summary>
    /// An NTSTATUS this decoder has no name for still gets the half that matters most: that the number
    /// is a WINDOWS status, not the program's own exit code, plus the hex to search for. Decoding only
    /// the codes on a list would leave the next unfamiliar one exactly as opaque as -1073741515 was.
    /// </summary>
    [Fact]
    public void Describe_StillSaysWindowsKilledItForAnUnlistedStatus()
    {
        var described = DarlingToolExitCode.Describe(StatusNoMemory);

        Assert.Contains("0xC0000017", described, StringComparison.Ordinal);
        Assert.Contains("Windows", described, StringComparison.Ordinal);
    }

    /// <summary>
    /// A tool's OWN exit code is left completely alone. initdb exits 1 on a bad option and pg_ctl status
    /// exits 3 for "not running"; dressing those up as Windows statuses would be a new lie in place of
    /// the old one.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(127)]
    public void Describe_LeavesAnOrdinaryExitCodeAsTheBareNumber(int exitCode)
    {
        Assert.Equal(exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture), DarlingToolExitCode.Describe(exitCode));
    }

    /// <summary>
    /// The diagnosis names BOTH causes the issue asks for and both checks that separate them, and it
    /// points at the directory the DLLs must be in rather than at "the install" in the abstract.
    /// </summary>
    [Fact]
    public void Diagnose_NamesBothCausesAndBothChecks()
    {
        var diagnosis = DarlingToolExitCode.Diagnose(StatusDllNotFound, InitDb);

        /* Cause 1: the bundled MSVC runtime, in the directory it is bundled into. */
        Assert.Contains(@"C:\PerformanceMonitorDarling\pg-runtime\pgsql\bin", diagnosis, StringComparison.Ordinal);
        Assert.Contains("vcruntime140.dll", diagnosis, StringComparison.Ordinal);
        Assert.Contains("vcruntime140_1.dll", diagnosis, StringComparison.Ordinal);
        Assert.Contains("msvcp140.dll", diagnosis, StringComparison.Ordinal);

        /* Cause 2: the service account cannot read the install tree. */
        Assert.Contains("NT SERVICE", diagnosis, StringComparison.Ordinal);

        /* Check 1: run the binary by hand. Check 2: the Windows log that names the module. */
        Assert.Contains("--version", diagnosis, StringComparison.Ordinal);
        Assert.Contains("Event Viewer", diagnosis, StringComparison.Ordinal);
    }

    /// <summary>
    /// The empty-output half of the report: the diagnosis has to say the blank field is EXPECTED for a
    /// loader failure. Leaving it unexplained is what made the field report read as "no information
    /// available" instead of "this is a load failure".
    /// </summary>
    [Fact]
    public void Diagnose_SaysAnEmptyOutputIsExpectedNotMissing()
    {
        var diagnosis = DarlingToolExitCode.Diagnose(StatusDllNotFound, InitDb);

        Assert.Contains("expected", diagnosis, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("loader", diagnosis, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(StatusEntryPointNotFound)]
    [InlineData(StatusInvalidImageFormat)]
    [InlineData(StatusDllInitFailed)]
    [InlineData(StatusAccessDenied)]
    public void Diagnose_CoversEveryLoaderStatusNotJustTheReportedOne(int exitCode)
    {
        var diagnosis = DarlingToolExitCode.Diagnose(exitCode, InitDb);

        Assert.Contains("vcruntime140.dll", diagnosis, StringComparison.Ordinal);
        Assert.Contains("NT SERVICE", diagnosis, StringComparison.Ordinal);
    }

    /// <summary>
    /// A CRASH is not a load failure, and telling an operator to go looking for missing DLLs after an
    /// access violation would be the same wrong-direction error the raw number caused — just pointed
    /// somewhere new. The process ran; its output is real; the DLL advice must not appear.
    /// </summary>
    [Fact]
    public void Diagnose_DoesNotSendACrashLookingForMissingDlls()
    {
        var diagnosis = DarlingToolExitCode.Diagnose(StatusAccessViolation, InitDb);

        Assert.DoesNotContain("vcruntime140", diagnosis, StringComparison.Ordinal);
        Assert.Contains("crash", diagnosis, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An ordinary non-zero exit gets no paragraph at all — initdb exiting 1 with a real error
    /// message on stderr needs no help from here, and burying that message under boilerplate would make
    /// the common failure worse to read.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    public void Diagnose_IsSilentForAnOrdinaryExitCode(int exitCode)
    {
        Assert.Equal(string.Empty, DarlingToolExitCode.Diagnose(exitCode, InitDb));
    }

    /// <summary>
    /// The <c>Output:</c> field itself. On a Windows status an empty capture is stated as expected; on an
    /// ordinary exit it is just absent, with no loader story attached to it.
    /// </summary>
    [Fact]
    public void FormatOutput_ExplainsABlankCaptureOnlyWhenWindowsKilledIt()
    {
        var loader = DarlingToolExitCode.FormatOutput(string.Empty, StatusDllNotFound);
        Assert.Contains("expected", loader, StringComparison.OrdinalIgnoreCase);

        var ordinary = DarlingToolExitCode.FormatOutput("   ", 1);
        Assert.DoesNotContain("expected", ordinary, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(ordinary), "a blank capture still has to render as something an operator can read");
    }

    [Fact]
    public void FormatOutput_PassesRealOutputThroughUntouched()
    {
        const string real = "initdb: error: directory \"C:\\pg\" exists but is not empty";

        Assert.Equal(real, DarlingToolExitCode.FormatOutput(real, 1));
        Assert.Equal(real, DarlingToolExitCode.FormatOutput(real, StatusDllNotFound));
    }

    /// <summary>
    /// The empirical half, and the reason this test uses a real process rather than a constant: it pins
    /// that a Windows status really does arrive at <see cref="DarlingManagedPostgres.RunToolAsync"/> as
    /// the signed number this decoder is built around, with an EMPTY capture — the two facts the whole
    /// fix rests on. <c>cmd /c exit</c> sets the same exit status the loader would; it is the status's
    /// journey through the runner that is under test, not how it was produced.
    /// </summary>
    [Fact]
    public async Task RunTool_SurfacesAWindowsStatusAsTheSignedFieldValueWithNoOutput()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "cmd.exe carries the Windows status; on other platforms this reports skipped, not vacuously passed.");

        var cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

        var (exitCode, output) = await DarlingManagedPostgres.RunToolAsync(
            cmd, "/c exit 3221225781", TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(-1073741515, exitCode);
        Assert.Equal(string.Empty, output);
        Assert.Contains("STATUS_DLL_NOT_FOUND", DarlingToolExitCode.Describe(exitCode), StringComparison.Ordinal);
    }
}
