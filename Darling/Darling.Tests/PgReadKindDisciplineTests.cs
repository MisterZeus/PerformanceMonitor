/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// The naive-UTC bind discipline for the PostgreSQL read family (#2213 round 2). The store's timestamp
/// columns are <c>timestamp without time zone</c>; a <c>Kind=Utc</c> DateTime makes Npgsql infer
/// timestamptz, and PostgreSQL resolves the mixed comparison by converting the NAIVE side at the store
/// session's TimeZone — which initdb takes from the host OS. East of UTC every fresh row falls outside the
/// freshness window and the three Tier 0 outage predictors silently never fire; west of UTC the window
/// stretches and alerts grade stale data. No error is raised anywhere, and every UTC-hosted test store
/// hides it, which is why this is a SOURCE pin rather than a live assertion: the live store that would
/// catch it is exactly the one nobody runs.
///
/// <para>The established adapter documents the same convention (DarlingAlertReadAdapter's NaiveUtcNow and
/// DarlingDataReader's AddTimestamp); this pin holds the NEW family to it. It scans for the hazard —
/// binding a window parameter or a raw <c>DateTime.UtcNow</c> without stripping Kind — rather than for the
/// idiom, so a refactor that binds safely through a different helper still passes.</para>
/// </summary>
public sealed class PgReadKindDisciplineTests
{
    [Fact]
    public void NoPgReaderBindsAKindUtcTimestamp()
    {
        var offenders = new List<string>();

        foreach (var path in PgReadFamilyFiles())
        {
            var source = File.ReadAllText(path);
            var name = Path.GetFileName(path);

            /* Bare window-parameter binds: AddWithValue(startUtc) / AddWithValue(endUtc). The callers hand
               these down from DateTime.UtcNow, so an unwrapped bind ships Kind=Utc. */
            foreach (Match match in Regex.Matches(
                source, @"AddWithValue\(\s*(?:startUtc|endUtc|sinceUtc|fromUtc|toUtc)\s*\)"))
            {
                offenders.Add(name + ": " + match.Value);
            }

            /* Direct now-arithmetic binds: AddWithValue(DateTime.UtcNow ...) — the alert adapter's
               original form. */
            foreach (Match match in Regex.Matches(
                source, @"AddWithValue\(\s*DateTime\.UtcNow"))
            {
                offenders.Add(name + ": " + match.Value);
            }
        }

        Assert.True(
            offenders.Count == 0,
            "PostgreSQL read binds a Kind=Utc timestamp against the store's naive columns: "
            + string.Join(", ", offenders)
            + ". Wrap the value in DateTime.SpecifyKind(..., DateTimeKind.Unspecified) at the bind (or a "
            + "NaiveUtcNow helper) — Kind=Utc infers timestamptz, the session zone shifts the window, and "
            + "east of UTC the Tier 0 alerts silently never fire.");
    }

    private static IEnumerable<string> PgReadFamilyFiles([CallerFilePath] string thisFile = "")
    {
        var testsDir = Path.GetDirectoryName(thisFile)!;
        var service = Path.GetFullPath(Path.Combine(testsDir, "..", "PerformanceMonitor.Darling.Service"));

        foreach (var reader in Directory.EnumerateFiles(Path.Combine(service, "Mcp"), "DarlingPg*Reader.cs"))
        {
            yield return reader;
        }

        yield return Path.Combine(service, "DarlingPostgresAlertReadAdapter.cs");
    }
}
