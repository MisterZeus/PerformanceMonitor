/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using PerformanceMonitor.Darling.Viewer;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// The definition behind <c>[Collection("darling-config-env")]</c>: the classes that must set the
/// process-wide <c>DARLING_CONFIG</c> environment variable to exercise the resolver that reads it.
///
/// <para>Same hazard, same remedy as <see cref="ViewerTimeStaticsCollection"/>. An environment variable is
/// process state, and xUnit runs separate collections in PARALLEL — so a test that sets DARLING_CONFIG to a
/// temp file can be mid-assertion while another test asks the resolver what it would pick, and the second
/// one fails on the first one's variable. Every member here saves and restores the previous value in a
/// <c>finally</c>, which makes them safe against each OTHER inside the collection; the
/// <see cref="CollectionDefinitionAttribute.DisableParallelization"/> is what makes them safe against the
/// rest of the assembly.</para>
/// </summary>
[CollectionDefinition("darling-config-env", DisableParallelization = true)]
public sealed class DarlingConfigEnvironmentCollection
{
}

/// <summary>
/// The viewer's startup self-description (#1954): which darling.json it read, and the non-secret summary
/// of what it parsed.
///
/// <para><b>The redaction test is the point of this file.</b> The summary is emitted to a log file and
/// rendered in the connection-failure overlay — two places an operator copies into a bug report — and the
/// connection string it summarizes carries a live database password. "We were careful not to print it" is
/// not a guarantee; a test that feeds a real password through and asserts the output cannot contain it is.
/// It goes red if the allowlist in <see cref="ViewerConfigDiagnostics"/> is ever replaced by anything that
/// copies the caller's string through.</para>
/// </summary>
[Collection("darling-config-env")]
public sealed class ViewerConfigDiagnosticsTests
{
    /// <summary>A value distinctive enough that its presence anywhere in the output is unambiguous.</summary>
    private const string LivePassword = "pw-9f3c1a7e-must-never-be-logged";

    private static string ByoConnectionString(string rootCertificate = "server.crt") =>
        $"Host=store.example.com;Port=5641;Username=viewer;Password={LivePassword};Database=darling;" +
        $"Search Path=collect,config,public;SSL Mode=VerifyFull;Root Certificate={rootCertificate}";

    [Fact]
    public void DescribeConnection_NeverEchoesThePassword()
    {
        var lines = ViewerConfigDiagnostics.DescribeConnection(ByoConnectionString(), managed: false);
        var text = string.Join(Environment.NewLine, lines);

        Assert.DoesNotContain(LivePassword, text, StringComparison.Ordinal);
        /* The keyword too: a summary that named "Password" without its value would still invite someone to
           "just add the value" later, and there is no diagnostic reason to mention it at all. */
        Assert.DoesNotContain("password", text, StringComparison.OrdinalIgnoreCase);

        /* A redaction assertion passes trivially against empty output, so pin that a real summary was in
           fact produced — otherwise this test would keep passing after the summary stopped working. */
        Assert.Contains("store.example.com", text, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildDetails_TheBlockShownInTheUiAndWrittenToTheLog_NeverEchoesThePassword()
    {
        /* The composed block, not just the connection half — this is the exact string that reaches
           MessageDetailsText and ViewerLogger, so it is the string the guarantee has to hold for. */
        var location = ViewerSettings.ResolveConfigLocation(@"C:\Darling\darling.json");
        var details = ViewerConfigDiagnostics.BuildDetails(location, ByoConnectionString(), managed: false);

        Assert.DoesNotContain(LivePassword, details, StringComparison.Ordinal);
        Assert.DoesNotContain("password", details, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"C:\Darling\darling.json", details, StringComparison.Ordinal);
        Assert.Contains("store.example.com", details, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeConnection_MalformedString_ReportsTheFailureWithoutEchoingTheString()
    {
        /* Npgsql's parse errors can quote the fragment they choked on, and that fragment can be the
           credential half — so an unparseable string reports the exception TYPE and nothing else. */
        var lines = ViewerConfigDiagnostics.DescribeConnection(
            $"Host=store.example.com;Port=NOT-A-NUMBER;Password={LivePassword}", managed: false);
        var text = string.Join(Environment.NewLine, lines);

        Assert.DoesNotContain(LivePassword, text, StringComparison.Ordinal);
        Assert.Contains("COULD NOT BE PARSED", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeConnection_ReportsEveryNonSecretFieldAnOperatorWouldCheck()
    {
        var lines = ViewerConfigDiagnostics.DescribeConnection(ByoConnectionString(), managed: false);

        Assert.Equal("store.example.com", ValueFor(lines, "Host"));
        Assert.Equal("5641", ValueFor(lines, "Port"));
        Assert.Equal("viewer", ValueFor(lines, "Username"));
        Assert.Equal("darling", ValueFor(lines, "Database"));
        Assert.Equal("VerifyFull", ValueFor(lines, "SSL Mode"));
        Assert.Equal("collect,config,public", ValueFor(lines, "Search Path"));
        Assert.Equal("server.crt", ValueFor(lines, "Root Certificate"));
    }

    [Fact]
    public void DescribeConnection_ManagedMode_SaysTheConnectionStringInTheFileIsNotRead()
    {
        /* The managed flag decides whether postgres.connectionString is consulted AT ALL — an operator
           editing it on a managed install is editing something nothing reads, which is exactly the kind of
           "right file, wrong value" confusion this summary exists to end. */
        var managed = ValueFor(
            ViewerConfigDiagnostics.DescribeConnection("Host=127.0.0.1;Port=5641;Username=admin;Database=darling", managed: true),
            "postgres.managed");
        Assert.StartsWith("true", managed, StringComparison.Ordinal);
        Assert.Contains("not read", managed, StringComparison.Ordinal);

        var byo = ValueFor(ViewerConfigDiagnostics.DescribeConnection(ByoConnectionString(), managed: false), "postgres.managed");
        Assert.StartsWith("false", byo, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeConnection_NoConnectionString_SaysNothingWasParsed_RatherThanInventingFields()
    {
        var text = string.Join(Environment.NewLine, ViewerConfigDiagnostics.DescribeConnection(null, managed: false));

        Assert.Contains("not loaded", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Host:", text, StringComparison.Ordinal);
        /* Nor a managed verdict — there was no file to read one out of. */
        Assert.DoesNotContain("postgres.managed", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The sharpest case in the whole feature: the documented bring-your-own string carries a BARE
    /// <c>Root Certificate=server.crt</c>, which Npgsql resolves against the process working directory — not
    /// the viewer's install directory and not darling.json's directory. So the same viewer launched from a
    /// shortcut and from a shell looks for the certificate in different places, and verify-full fails in one
    /// of them with nothing said about where it looked.
    /// </summary>
    [Fact]
    public void DescribeConnection_RelativeRootCertificate_ResolvesAgainstTheWorkingDirectory_AndReportsExistence()
    {
        var root = Directory.CreateTempSubdirectory("darling-viewer-cert-");
        try
        {
            var expected = Path.Combine(root.FullName, "server.crt");

            var missing = ViewerConfigDiagnostics.DescribeConnection(
                ByoConnectionString(), managed: false, workingDirectory: root.FullName);
            Assert.Equal(expected, ValueFor(missing, "resolves to"));
            Assert.Equal(root.FullName, ValueFor(missing, "relative to"));
            Assert.Equal("NO", ValueFor(missing, "exists"));

            File.WriteAllText(expected, "-----BEGIN CERTIFICATE-----");
            var present = ViewerConfigDiagnostics.DescribeConnection(
                ByoConnectionString(), managed: false, workingDirectory: root.FullName);
            Assert.Equal("yes", ValueFor(present, "exists"));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void DescribeConnection_AbsoluteRootCertificate_ReportsItAsGiven_WithNoRelativeToLine()
    {
        var root = Directory.CreateTempSubdirectory("darling-viewer-abscert-");
        try
        {
            var certificate = Path.Combine(root.FullName, "pinned.crt");
            File.WriteAllText(certificate, "-----BEGIN CERTIFICATE-----");

            var lines = ViewerConfigDiagnostics.DescribeConnection(
                ByoConnectionString(certificate), managed: false, workingDirectory: root.FullName);

            Assert.Equal(certificate, ValueFor(lines, "resolves to"));
            Assert.Equal("yes", ValueFor(lines, "exists"));
            /* An absolute path has no working-directory dependency, so claiming one would be noise. */
            Assert.DoesNotContain(lines, l => l.TrimStart().StartsWith("relative to:", StringComparison.Ordinal));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void DescribeConnection_ManagedLoopback_ReportsNoCertificate()
    {
        var lines = ViewerConfigDiagnostics.DescribeConnection(
            "Host=127.0.0.1;Port=5641;Username=admin;Database=darling", managed: true);

        Assert.Equal("(not set)", ValueFor(lines, "Root Certificate"));
    }

    // ── Which darling.json won ────────────────────────────────────────────────────────────

    [Fact]
    public void ResolveConfigLocation_CommandLineArgument_IsReportedAsSuch_WithItsExistence()
    {
        var missing = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "darling.json");

        var location = ViewerSettings.ResolveConfigLocation(missing);

        Assert.Equal(ViewerConfigSource.CommandLine, location.Source);
        Assert.Equal(missing, location.Path);
        Assert.False(location.Exists);
    }

    /// <summary>
    /// The failure this feature was reported for: DARLING_CONFIG is set AND a darling.json sits beside the
    /// binary, and the operator cannot tell which one is live. The variable wins, and now says so.
    /// </summary>
    [Fact]
    public void ResolveConfigLocation_EnvironmentVariable_OutranksAFileBesideTheBinary_AndIsReportedAsSuch()
    {
        var saved = Environment.GetEnvironmentVariable("DARLING_CONFIG");
        var root = Directory.CreateTempSubdirectory("darling-viewer-envsource-");
        try
        {
            var viewerDirectory = Path.Combine(root.FullName, "viewer");
            Directory.CreateDirectory(viewerDirectory);
            File.WriteAllText(Path.Combine(viewerDirectory, "darling.json"), "{}");

            var fromEnvironment = Path.Combine(root.FullName, "elsewhere.json");
            File.WriteAllText(fromEnvironment, "{}");
            Environment.SetEnvironmentVariable("DARLING_CONFIG", fromEnvironment);

            var location = ViewerSettings.ResolveConfigLocation(baseDirectory: viewerDirectory);

            Assert.Equal(ViewerConfigSource.EnvironmentVariable, location.Source);
            Assert.Equal(fromEnvironment, location.Path);
            Assert.True(location.Exists);

            var text = string.Join(Environment.NewLine, ViewerConfigDiagnostics.DescribeConfigLocation(location));
            Assert.Contains("DARLING_CONFIG", text, StringComparison.Ordinal);
            Assert.Contains(fromEnvironment, text, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DARLING_CONFIG", saved);
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void ResolveConfigLocation_ProbedLocations_DistinguishBesideTheViewerFromTheServiceRoot()
    {
        var saved = Environment.GetEnvironmentVariable("DARLING_CONFIG");
        Environment.SetEnvironmentVariable("DARLING_CONFIG", null);
        var root = Directory.CreateTempSubdirectory("darling-viewer-probesource-");
        try
        {
            var viewerDirectory = Path.Combine(root.FullName, "viewer");
            Directory.CreateDirectory(viewerDirectory);

            /* Nothing anywhere: report the viewer's own directory, and say it is not there. */
            var nothing = ViewerSettings.ResolveConfigLocation(baseDirectory: viewerDirectory);
            Assert.Equal(ViewerConfigSource.BesideViewer, nothing.Source);
            Assert.False(nothing.Exists);

            /* The shipped-zip layout: viewer\ under the service root, darling.json beside the SERVICE. */
            var atServiceRoot = Path.Combine(root.FullName, "darling.json");
            File.WriteAllText(atServiceRoot, "{}");
            var fromServiceRoot = ViewerSettings.ResolveConfigLocation(baseDirectory: viewerDirectory);
            Assert.Equal(ViewerConfigSource.ServiceRoot, fromServiceRoot.Source);
            Assert.Equal(atServiceRoot, fromServiceRoot.Path);
            Assert.True(fromServiceRoot.Exists);

            /* Beside the viewer still wins when both exist. */
            var besideViewer = Path.Combine(viewerDirectory, "darling.json");
            File.WriteAllText(besideViewer, "{}");
            var fromBesideViewer = ViewerSettings.ResolveConfigLocation(baseDirectory: viewerDirectory);
            Assert.Equal(ViewerConfigSource.BesideViewer, fromBesideViewer.Source);
            Assert.Equal(besideViewer, fromBesideViewer.Path);
            Assert.True(fromBesideViewer.Exists);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DARLING_CONFIG", saved);
            root.Delete(recursive: true);
        }
    }

    /// <summary>
    /// The diagnostics and the load must never describe different files. <c>ResolveConfigPath</c> is a
    /// projection of <c>ResolveConfigLocation</c> rather than a second copy of the rules, and this pins it —
    /// a re-implementation that drifted would make the whole feature actively misleading.
    /// </summary>
    [Fact]
    public void ResolveConfigLocation_AndResolveConfigPath_CannotDisagree()
    {
        var saved = Environment.GetEnvironmentVariable("DARLING_CONFIG");
        Environment.SetEnvironmentVariable("DARLING_CONFIG", null);
        var root = Directory.CreateTempSubdirectory("darling-viewer-agree-");
        try
        {
            var viewerDirectory = Path.Combine(root.FullName, "viewer");
            Directory.CreateDirectory(viewerDirectory);

            Assert.Equal(
                ViewerSettings.ResolveConfigPath(baseDirectory: viewerDirectory),
                ViewerSettings.ResolveConfigLocation(baseDirectory: viewerDirectory).Path);

            File.WriteAllText(Path.Combine(root.FullName, "darling.json"), "{}");
            Assert.Equal(
                ViewerSettings.ResolveConfigPath(baseDirectory: viewerDirectory),
                ViewerSettings.ResolveConfigLocation(baseDirectory: viewerDirectory).Path);

            Environment.SetEnvironmentVariable("DARLING_CONFIG", @"C:\from\env\darling.json");
            Assert.Equal(
                ViewerSettings.ResolveConfigPath(baseDirectory: viewerDirectory),
                ViewerSettings.ResolveConfigLocation(baseDirectory: viewerDirectory).Path);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DARLING_CONFIG", saved);
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void DescribeConfigLocation_RelativeConfiguredPath_ReportsTheAbsolutePathItActuallyOpens()
    {
        /* A relative DARLING_CONFIG or command-line path is precisely the case where the operator's idea of
           the path and the viewer's are not the same string, so both are reported. */
        var location = ViewerSettings.ResolveConfigLocation("conf/darling.json");
        var lines = ViewerConfigDiagnostics.DescribeConfigLocation(location);

        Assert.Equal(Path.GetFullPath("conf/darling.json"), ValueFor(lines, "darling.json path"));
        Assert.Equal("conf/darling.json", ValueFor(lines, "as configured"));
        Assert.Contains("command-line", ValueFor(lines, "darling.json source"), StringComparison.Ordinal);
        Assert.Equal("NO", ValueFor(lines, "darling.json exists"));
    }

    // ── The seam: the UI failure path cannot ship without the diagnostics ─────────────────

    /// <summary>
    /// Every connection/config failure in the viewer shell renders through <c>ShowConnectionFailure</c>,
    /// which attaches the diagnostics block; the raw <c>ShowMessage</c> renderer has exactly one caller,
    /// which is that helper. The compiler already forces a details argument at every call site, but it
    /// cannot stop a new branch from passing null — so the invariant that matters is structural: nothing in
    /// the shell calls the renderer directly. Source-parsed, like the repo's other seam pins, because the
    /// alternative is standing up a WPF window on an STA thread to assert a text property.
    /// </summary>
    [Fact]
    public void EveryFailureSurfaceInTheShellGoesThroughTheDiagnosticsCarryingHelper()
    {
        var source = File.ReadAllText(Path.Combine(ViewerDirectory(), "MainWindow.xaml.cs"));

        /* The definition reads "private void ShowMessage(", so exclude a preceding "void ". */
        var directCalls = Regex.Matches(source, @"(?<!void )ShowMessage\(").Count;
        Assert.True(
            directCalls == 1,
            $"MainWindow.xaml.cs calls ShowMessage directly {directCalls} time(s); exactly one is expected " +
            "(inside ShowConnectionFailure). A failure surface that calls ShowMessage itself shows the " +
            "operator a message with no configuration context — route it through ShowConnectionFailure, or " +
            "update this pin deliberately if a message genuinely has none.");

        var helperUses = Regex.Matches(source, @"ShowConnectionFailure\(").Count;
        Assert.True(
            helperUses >= 6,
            $"Expected the diagnostics-carrying helper at the config-read, config-missing, schema-gate, " +
            $"store-unreachable, connect-failed and store-read failure surfaces (plus its definition); found {helperUses}.");
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────

    /// <summary>The value on the single line whose label is <paramref name="label"/> (labels are padded, so
    /// this trims rather than splitting on a fixed column).</summary>
    private static string ValueFor(IReadOnlyList<string> lines, string label)
    {
        var prefix = label + ":";
        var line = Assert.Single(lines, l => l.TrimStart().StartsWith(prefix, StringComparison.Ordinal));
        return line.Trim()[prefix.Length..].Trim();
    }

    /// <summary>The Viewer project directory, resolved from this test file's compile-time path.</summary>
    private static string ViewerDirectory([CallerFilePath] string thisFile = "")
    {
        var testDirectory = Path.GetDirectoryName(thisFile)!;
        return Path.GetFullPath(Path.Combine(testDirectory, "..", "PerformanceMonitor.Darling.Viewer"));
    }
}
