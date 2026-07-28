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
using PerformanceMonitor.Darling.Service;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// Pins the install-directory report added after a production field instance verified SILENT: #1775's
/// store-copy report scans siblings of the DATA directory under <c>%ProgramData%</c>, and the field's seven
/// hand-made directories are under the INSTALL directory — a different parent, so nothing was ever going to
/// name them. They also carry no <c>PG_VERSION</c> (seven copies of a ~280 GB store do not fit on a 500 GB
/// volume, so they are small snapshots, not clusters), which would have kept them invisible to a
/// store-shaped test even in the right place.
///
/// <para>The whole feature is a classification, so the tests are about classification going BOTH ways: a
/// foreign directory must be named, and a directory the product itself put there must never be called
/// foreign. The second half is the one that decides whether an operator believes the first.</para>
/// </summary>
public sealed class DarlingInstallDirectoryReportTests
{
    /// <summary>The field's own naming, used as the acceptance case rather than a stand-in.</summary>
    private const string FieldDirectoryPrefix = "_rollback_manual_";

    [Fact]
    public void Report_NamesForeignDirectories_WithTheirSize()
    {
        var root = Directory.CreateTempSubdirectory("darling-installdir-foreign-");
        try
        {
            PlantProductLayout(root.FullName);
            var manual = PlantDirectory(root.FullName, FieldDirectoryPrefix + "20260720", "snapshot.bak", 4096);

            var log = new CapturingLogger();
            DarlingInstallDirectoryReport.Report(root.FullName, log);
            var lines = log.ToString();

            Assert.Contains("are not part of the product's layout", lines, StringComparison.Ordinal);
            Assert.Contains("Directory not part of the product's layout: " + manual, lines, StringComparison.Ordinal);

            /* Reported, and still there — the never-delete property, asserted rather than assumed. */
            Assert.True(Directory.Exists(manual));
        }
        finally
        {
            TryDeleteTree(root.FullName);
        }
    }

    /// <summary>
    /// The acceptance case for the field box: seven directories in the reported shape produce ONE summary
    /// line and SEVEN per-directory lines, and all seven survive.
    /// </summary>
    [Fact]
    public void Report_SevenFieldDirectories_ProduceOneSummaryAndSevenLines_AndAllSurvive()
    {
        var root = Directory.CreateTempSubdirectory("darling-installdir-seven-");
        try
        {
            PlantProductLayout(root.FullName);

            var planted = new List<string>();
            foreach (var stamp in new[] { "20260720", "20260720b", "20260720c", "20260721", "20260722", "20260722b", "20260724" })
            {
                planted.Add(PlantDirectory(root.FullName, FieldDirectoryPrefix + stamp, "snapshot.bak", 1024));
            }

            var log = new CapturingLogger();
            DarlingInstallDirectoryReport.Report(root.FullName, log);
            var lines = log.ToString();

            Assert.Equal(1, CountOccurrences(lines, "are not part of the product's layout"));
            Assert.Equal(7, CountOccurrences(lines, "Directory not part of the product's layout: "));
            Assert.Contains("7 director(ies) in the install directory", lines, StringComparison.Ordinal);

            foreach (var directory in planted)
            {
                Assert.Contains(directory, lines, StringComparison.Ordinal);
                Assert.True(Directory.Exists(directory), $"{directory} must survive the report");
            }
        }
        finally
        {
            TryDeleteTree(root.FullName);
        }
    }

    /// <summary>
    /// THE credibility pin. Every directory the product's own layout puts here — shipped, packaged, or
    /// created at runtime — must go unmentioned. Telling an operator the product did not install a directory
    /// the product installed is how the report stops being read, and it is the failure mode that a report
    /// classifying by ELIMINATION invites.
    /// </summary>
    [Fact]
    public void Report_NeverCallsAProductDirectoryForeign()
    {
        var root = Directory.CreateTempSubdirectory("darling-installdir-owned-");
        try
        {
            var owned = PlantProductLayout(root.FullName);

            var log = new CapturingLogger();
            DarlingInstallDirectoryReport.Report(root.FullName, log);
            var lines = log.ToString();

            /* Nothing at all is reported when the install directory holds only the product's own layout. */
            Assert.DoesNotContain("not part of the product's layout", lines, StringComparison.Ordinal);

            foreach (var directory in owned)
            {
                Assert.DoesNotContain(directory, lines, StringComparison.Ordinal);
            }
        }
        finally
        {
            TryDeleteTree(root.FullName);
        }
    }

    /// <summary>
    /// A locale directory is recognised STRUCTURALLY — everything in it is a <c>.resources.dll</c> — not by
    /// a list of culture names. A dependency package can add a culture without a line of our code changing,
    /// and a hardcoded list would then have the product reporting its own shipped directory as foreign. The
    /// invented culture below is the point: it is in no list anywhere.
    /// </summary>
    [Fact]
    public void Report_TreatsAnyLocaleShapedDirectoryAsTheProducts_EvenAnUnlistedCulture()
    {
        var root = Directory.CreateTempSubdirectory("darling-installdir-locale-");
        try
        {
            PlantProductLayout(root.FullName);
            var newCulture = PlantDirectory(root.FullName, "nl-BE", "Npgsql.resources.dll", 128);

            var log = new CapturingLogger();
            DarlingInstallDirectoryReport.Report(root.FullName, log);

            Assert.DoesNotContain(newCulture, log.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteTree(root.FullName);
        }
    }

    /// <summary>
    /// A locale-NAMED directory holding something other than satellite assemblies is still foreign — the
    /// structural test must not become a naming loophole that hides a directory by calling it <c>de</c>.
    /// </summary>
    [Fact]
    public void Report_ALocaleNamedDirectoryHoldingOtherFiles_IsStillForeign()
    {
        var root = Directory.CreateTempSubdirectory("darling-installdir-fakelocale-");
        try
        {
            PlantProductLayout(root.FullName);
            var impostor = PlantDirectory(root.FullName, "de-AT", "backup.zip", 2048);

            var log = new CapturingLogger();
            DarlingInstallDirectoryReport.Report(root.FullName, log);

            Assert.Contains("Directory not part of the product's layout: " + impostor, log.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteTree(root.FullName);
        }
    }

    /// <summary>
    /// Budget exhaustion degrades a directory's SIZE and never its presence. A report whose only job is to
    /// tell an operator these directories exist must not drop one because a walk ran long — so a spent
    /// budget produces an "at least" line, not silence. Deadline in the past forces the exhausted path.
    /// </summary>
    [Fact]
    public void Report_WhenTheSizeProbeIsExhausted_StillReportsEveryDirectory()
    {
        var root = Directory.CreateTempSubdirectory("darling-installdir-budget-");
        try
        {
            PlantProductLayout(root.FullName);

            /* Enough files that the deadline check — every 512 entries — is actually reached. */
            var big = Path.Combine(root.FullName, FieldDirectoryPrefix + "20260724");
            Directory.CreateDirectory(big);
            for (var i = 0; i < 1200; i++)
            {
                File.WriteAllText(Path.Combine(big, $"f{i}.bin"), "x");
            }

            /* An already-spent budget, so the exhausted branch is entered rather than approximated. Running
               the REPORT this way is the point: measuring the helper alone would leave the branch that
               decides whether a directory still gets NAMED completely unexercised. */
            var log = new CapturingLogger();
            DarlingInstallDirectoryReport.Report(root.FullName, log, TimeSpan.Zero);
            var lines = log.ToString();

            /* Named, with an honest lower bound and the budget that ran out... */
            Assert.Contains("Directory not part of the product's layout: " + big, lines, StringComparison.Ordinal);
            Assert.Contains("at least", lines, StringComparison.Ordinal);
            Assert.Contains("size probe did not finish walking it", lines, StringComparison.Ordinal);

            /* ...and the summary total is marked approximate too, rather than stating a number it did not
               finish computing. */
            Assert.Contains("are holding at least ", lines, StringComparison.Ordinal);

            Assert.True(Directory.Exists(big));
        }
        finally
        {
            TryDeleteTree(root.FullName);
        }
    }

    /// <summary>
    /// An empty directory is reported rather than excused. It cannot be a satellite-resource directory (the
    /// product ships files in those), and naming something harmless is the safe direction for a report that
    /// never deletes.
    /// </summary>
    [Fact]
    public void Report_AnEmptyDirectory_IsReported()
    {
        var root = Directory.CreateTempSubdirectory("darling-installdir-empty-");
        try
        {
            PlantProductLayout(root.FullName);
            var empty = Path.Combine(root.FullName, "leftover");
            Directory.CreateDirectory(empty);

            var log = new CapturingLogger();
            DarlingInstallDirectoryReport.Report(root.FullName, log);

            Assert.Contains("Directory not part of the product's layout: " + empty, log.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteTree(root.FullName);
        }
    }

    [Fact]
    public void Report_AMissingInstallDirectory_IsSilentRatherThanThrowing()
    {
        var log = new CapturingLogger();
        DarlingInstallDirectoryReport.Report(Path.Combine(Path.GetTempPath(), "darling-does-not-exist-" + Guid.NewGuid().ToString("N")), log);
        Assert.Equal(string.Empty, log.ToString());
    }

    /// <summary>
    /// The product's own layout: the packaged directories, the runtime-created ones, and a representative
    /// satellite-resource directory. Returns them so a caller can assert none was named.
    /// </summary>
    private static List<string> PlantProductLayout(string root)
    {
        var planted = new List<string>();

        /* viewer + runtimes + wwwroot ship; pg-runtime is extracted on first run and pg-runtime-prev is the
           rescued previous runtime. */
        planted.Add(PlantDirectory(root, "viewer", "PerformanceMonitor.Darling.Viewer.exe", 64));
        planted.Add(PlantDirectory(root, "runtimes", Path.Combine("win-x64", "native", "e_sqlite3.dll"), 64));
        planted.Add(PlantDirectory(root, "wwwroot", "index.html", 64));
        planted.Add(PlantDirectory(root, "pg-runtime", Path.Combine("pgsql", "bin", "pg_ctl.exe"), 64));
        planted.Add(PlantDirectory(root, "pg-runtime" + DarlingStoreUpgrade.PreviousRuntimeSuffix, Path.Combine("pgsql", "bin", "pg_ctl.exe"), 64));

        /* One real satellite-resource directory from the measured publish. */
        planted.Add(PlantDirectory(root, "ja", "Microsoft.Data.SqlClient.resources.dll", 64));

        /* Files at the root are not directories and are never candidates. */
        File.WriteAllText(Path.Combine(root, "darling.json"), "{}");
        File.WriteAllText(Path.Combine(root, "pg-runtime.zip"), "zip");

        return planted;
    }

    private static string PlantDirectory(string root, string name, string relativeFile, int bytes)
    {
        var directory = Path.Combine(root, name);
        var file = Path.Combine(directory, relativeFile);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, new string('x', bytes));
        return directory;
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }

    private static void TryDeleteTree(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            /* A leftover temp tree is not a test failure. */
        }
    }

    /// <summary>Records every line the report writes, so the classification can be asserted rather than assumed.</summary>
    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger
    {
        private readonly System.Text.StringBuilder _lines = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_lines)
            {
                _lines.Append('[').Append(logLevel).Append("] ").AppendLine(formatter(state, exception));
            }
        }

        public override string ToString()
        {
            lock (_lines)
            {
                return _lines.ToString();
            }
        }
    }
}
