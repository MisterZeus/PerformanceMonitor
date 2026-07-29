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
using System.Text.RegularExpressions;
using PerformanceMonitor.Darling.Service;
using PerformanceMonitor.Darling.Storage;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// Drift guard between the SHIPPED bring-your-own-Postgres provisioning script
/// (<c>Darling/tools/provision-roles.sql</c>) and the C# list it hand-mirrors,
/// <see cref="DarlingManagedRoles.ViewerRestrictedConfigTables"/>.
///
/// <para><b>Why this exists:</b> the fail-closed viewer column ACL (#1262) is authored ONCE in C# and applied
/// two ways. Managed mode generates it from the C# list at every service start
/// (<see cref="DarlingManagedRoles.BuildViewerColumnAclSql"/>), so it is correct by construction. BYO mode has
/// no service to generate anything — the operator runs the .sql by hand, and that file's
/// <c>GRANT SELECT (…)</c> lists are a HAND COPY of the same columns. Nothing tied the two together, so they
/// drifted, silently, across three releases: the C# list gained
/// <c>config_monitored_servers.alert_delivery_mode_override</c> and
/// <c>config_notification.generic_body_template</c> / <c>generic_proxy</c>, the .sql was never updated, and a
/// BYO <c>viewer</c> seat SQLSTATE-42501'd on the two projections written FOR it —
/// <c>ViewerDataService.MonitoredServersSelectSql</c> (the Manage Servers list + sidebar reconcile) and
/// <c>NotificationSelectNoSecretSql</c> (the Settings prefill) — reads a managed seat served fine (#1639). The
/// failure mode is quiet in exactly the wrong direction: the columns still exist, the script still runs clean,
/// and only a read from the least-privilege role ever notices.
///
/// <para>The existing coverage could not catch it. <see cref="DarlingManagedRolesTests"/> asserts the
/// GENERATED DDL against the same C# list (both sides of that comparison move together), and
/// <see cref="DarlingSecuritySplitLiveTests"/> proves the carve against a real store — but as the gated-live
/// suite, using the C#-generated DDL, never the shipped .sql. This test reads the REAL file (copied beside the
/// test binary by the csproj, so there is no second copy to go stale and no repo-root path walking), parses
/// out every <c>GRANT SELECT (columns) ON schema.table TO role</c>, and asserts SET-EQUALITY against the C#
/// list — so a column added to either side without the other now fails an UNGATED test on every build,
/// database or not.</para>
///
/// <para>The guard is itself guarded: <see cref="ParsedViewerAcl_Comparison_FailsOnAnInjectedDrift"/> runs the
/// identical comparison against a mutated copy of the file and asserts it reports the difference, so a parser
/// that silently matched nothing (a reformatted GRANT, a renamed file) can never pass as "no drift".</para>
/// </summary>
public sealed class ProvisionRolesAclDriftTests
{
    /// <summary>
    /// <c>GRANT SELECT (col, col, …) ON schema.table TO role;</c> — the column-level form. A grant naming
    /// MORE than one role (<c>TO admin, viewer</c>) deliberately does NOT match: the trailing <c>;</c> is
    /// required, so a widened grant drops out of the parse and fails the table-set assertion rather than
    /// being read as a viewer-only carve.
    /// </summary>
    private static readonly Regex ColumnGrant = new(
        @"GRANT\s+SELECT\s*\(\s*(?<columns>[^)]+?)\s*\)\s*ON\s+(?<schema>[A-Za-z_][A-Za-z0-9_]*)\.(?<table>[A-Za-z_][A-Za-z0-9_]*)\s+TO\s+(?<role>[A-Za-z_][A-Za-z0-9_]*)\s*;",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary><c>REVOKE SELECT ON schema.table FROM role;</c> — the carve's fail-closed half.</summary>
    private static readonly Regex TableRevoke = new(
        @"REVOKE\s+SELECT\s+ON\s+(?<schema>[A-Za-z_][A-Za-z0-9_]*)\.(?<table>[A-Za-z_][A-Za-z0-9_]*)\s+FROM\s+(?<role>[A-Za-z_][A-Za-z0-9_]*)\s*;",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private const string ViewerRole = DarlingManagedPostgres.ViewerRoleName;
    private const string AdminRole = DarlingManagedPostgres.AdminRoleName;
    private const string McpRole = DarlingManagedPostgres.McpRoleName;
    private const string ConfigSchema = PgSchemaGenerator.ConfigSchema;

    [Fact]
    public void ProvisionRolesSql_ViewerColumnGrants_MatchTheCSharpAclExactly()
    {
        var granted = ParseColumnGrants(StripComments(ReadProvisionRolesSql()), ViewerRole);

        /* Every secret-bearing config table the C# carves must be carved in the shipped script too — no
           missing table (its secret columns would stay readable through the blanket schema grant) and no
           extra table (a stale carve on a table the C# no longer restricts). */
        Assert.Equal(
            DarlingManagedRoles.ViewerRestrictedConfigTables.Select(a => a.Table).OrderBy(t => t, StringComparer.Ordinal),
            granted.Keys.OrderBy(t => t, StringComparer.Ordinal));

        foreach (var acl in DarlingManagedRoles.ViewerRestrictedConfigTables)
        {
            var sqlColumns = granted[acl.Table];

            /* A column listed twice would pass a set comparison while reading as an editing mistake. */
            Assert.Equal(sqlColumns.Count, sqlColumns.Distinct(StringComparer.Ordinal).Count());

            /* THE assertion: set-equality in BOTH directions. A column in C# but not the .sql is the #1639
               bug (a BYO viewer seat is denied a read a managed seat serves); a column in the .sql but not
               C# is the opposite — a BYO viewer reading something managed mode deliberately withholds. */
            Assert.Equal(
                acl.NonSecretColumns.OrderBy(c => c, StringComparer.Ordinal),
                sqlColumns.OrderBy(c => c, StringComparer.Ordinal));
        }
    }

    [Fact]
    public void ProvisionRolesSql_EveryCarvedTable_HasThePairedRevoke()
    {
        var sql = StripComments(ReadProvisionRolesSql());

        /* The carve is blanket-grant, then REVOKE, then GRANT-columns. Without the REVOKE the blanket schema
           grant still stands and the column GRANT is a no-op ADDITION — the script would LOOK carved while
           viewer read every secret column. That makes the statement ORDER load-bearing, so pin it. */
        var blanketGrantAt = sql.IndexOf($"GRANT SELECT ON ALL TABLES IN SCHEMA {ConfigSchema}", StringComparison.Ordinal);
        Assert.True(blanketGrantAt >= 0, "provision-roles.sql must still grant the blanket config SELECT the carve narrows.");

        var revoked = TableRevoke.Matches(sql)
            .Where(m => string.Equals(m.Groups["role"].Value, ViewerRole, StringComparison.Ordinal))
            .Select(m => m.Groups["table"].Value)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var acl in DarlingManagedRoles.ViewerRestrictedConfigTables)
        {
            Assert.Contains(acl.Table, revoked);

            /* The REVOKE must come AFTER the blanket grant (else it revokes nothing) and BEFORE the column
               GRANT (else it strips the very carve it was meant to enable). */
            var revokeAt = sql.IndexOf($"REVOKE SELECT ON {ConfigSchema}.{acl.Table} FROM {ViewerRole};", StringComparison.Ordinal);
            var grantAt = sql.IndexOf($"ON {ConfigSchema}.{acl.Table} TO {ViewerRole};", StringComparison.Ordinal);
            Assert.True(revokeAt > blanketGrantAt && grantAt > revokeAt,
                $"provision-roles.sql must REVOKE viewer's table-wide SELECT on {acl.Table} after the blanket config grant and before re-granting the non-secret columns.");

            /* The carve is viewer-only: admin writes these tables and keeps its table-wide SELECT. */
            Assert.DoesNotContain($"REVOKE SELECT ON {ConfigSchema}.{acl.Table} FROM {AdminRole}", sql, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ProvisionRolesSql_GrantsNoSecretColumn_ToAnyRole()
    {
        var sql = StripComments(ReadProvisionRolesSql());

        /* Belt over the per-table set comparison: no credential blob, webhook URL, or Authorization-header
           column may appear in ANY column grant in the shipped script, whatever role it names. */
        var everyGrantedColumn = ColumnGrant.Matches(sql)
            .SelectMany(m => SplitColumns(m.Groups["columns"].Value))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var secret in DarlingManagedRoles.ViewerRestrictedConfigTables.SelectMany(a => a.SecretColumns))
        {
            Assert.DoesNotContain(secret, everyGrantedColumn);
        }
    }

    [Fact]
    public void ProvisionRolesSql_ProvisionsAdminAndViewerOnly_NeverTheMcpRole()
    {
        var sql = StripComments(ReadProvisionRolesSql());

        /* Managed mode applies the SAME carve to a third role, mcp (BuildViewerColumnAclSql(config, "mcp")),
           and the shipped script deliberately does NOT mirror that: the network MCP endpoint is
           managed-mode-only, so a BYO operator's own PostgreSQL governs any MCP exposure it wants (see the
           DarlingManagedRoles summary and the BYO section of Darling/README.md). Pin the asymmetry as
           DELIBERATE, so a future reader closing "drift" does not add a half-provisioned mcp role: the only
           correct fix if BYO ever gains one is to add its roles AND its carve together. */
        Assert.DoesNotMatch(new Regex($@"\b{McpRole}\b", RegexOptions.IgnoreCase), sql);

        /* Correspondingly, every column-level grant in the script names the viewer role. */
        foreach (Match match in ColumnGrant.Matches(sql))
        {
            Assert.Equal(ViewerRole, match.Groups["role"].Value);
            Assert.Equal(ConfigSchema, match.Groups["schema"].Value);
        }
    }

    [Fact]
    public void ParsedViewerAcl_Comparison_FailsOnAnInjectedDrift()
    {
        /* Guard the guard. A drift test that cannot fail is worthless, and the ways this one could silently
           stop working (a reformatted GRANT, a renamed/moved file, a regex that matches nothing) all look
           identical to "no drift". Mutate the real file's text three ways and assert the SAME comparison the
           tests above run reports each one. */
        var real = StripComments(ReadProvisionRolesSql());
        var acl = DarlingManagedRoles.ViewerRestrictedConfigTables[0];
        var droppedColumn = acl.NonSecretColumns[^1];

        Assert.True(MatchesCSharpAcl(real), "The unmutated provision-roles.sql must match the C# ACL.");

        /* 1. The #1639 shape: the C# list gains a column the .sql never got. */
        Assert.False(
            MatchesCSharpAcl(MutateGrantColumns(real, acl.Table,
                columns => columns.Where(c => !string.Equals(c, droppedColumn, StringComparison.Ordinal)))),
            $"Removing {acl.Table}.{droppedColumn} from the .sql must be detected as drift.");

        /* 2. The reverse: the .sql grants a column the C# list does not classify as non-secret. */
        Assert.False(
            MatchesCSharpAcl(MutateGrantColumns(real, acl.Table, columns => columns.Append("not_a_real_column"))),
            "A column granted in the .sql but absent from the C# list must be detected as drift.");

        /* 3. A whole carve deleted (the table falls back to the blanket schema grant — secrets exposed). */
        Assert.False(MatchesCSharpAcl(ColumnGrant.Replace(real, m =>
                string.Equals(m.Groups["table"].Value, acl.Table, StringComparison.Ordinal) ? string.Empty : m.Value)),
            $"Deleting the {acl.Table} column grant entirely must be detected as drift.");
    }

    /* ─────────────────────────── parsing helpers ─────────────────────────── */

    /// <summary>
    /// The shipped script, copied beside the test binary by the csproj (<c>Link="Fixtures\provision-roles.sql"</c>)
    /// so the test parses the REAL file with no hardcoded repo path and no second copy that could go stale.
    /// A missing file FAILS rather than skipping — a guard that quietly disappears is the thing being guarded against.
    /// </summary>
    private static string ReadProvisionRolesSql()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "provision-roles.sql");
        Assert.True(File.Exists(path),
            $"provision-roles.sql was not copied to the test output ({path}) — check the None/Link item in Darling.Tests.csproj.");
        return File.ReadAllText(path);
    }

    /// <summary>
    /// Blanks <c>--</c> line comments so a GRANT quoted in prose is never parsed as DDL, quote-aware so a
    /// <c>--</c> inside a string literal (the RAISE EXCEPTION messages) is left alone. The script uses only
    /// line comments; the assertion below fails loud if a block comment is ever introduced, rather than
    /// letting this under-strip.
    /// </summary>
    private static string StripComments(string sql)
    {
        Assert.DoesNotContain("/*", sql, StringComparison.Ordinal);

        var kept = new List<string>();
        foreach (var line in sql.Split('\n'))
        {
            var inQuote = false;
            var cut = -1;
            for (var i = 0; i < line.Length; i++)
            {
                if (line[i] == '\'')
                {
                    inQuote = !inQuote;
                }
                else if (!inQuote && line[i] == '-' && i + 1 < line.Length && line[i + 1] == '-')
                {
                    cut = i;
                    break;
                }
            }

            kept.Add(cut < 0 ? line : line[..cut]);
        }

        return string.Join("\n", kept);
    }

    /// <summary>
    /// Every column-level SELECT grant to <paramref name="role"/>, keyed by table. Repeated grants on one
    /// table are UNIONed because that is what Postgres does with them (grants are additive), so splitting a
    /// list across two statements reads the same to this test as writing it as one.
    /// </summary>
    private static Dictionary<string, IReadOnlyList<string>> ParseColumnGrants(string sql, string role) =>
        ColumnGrant.Matches(sql)
            .Where(m => string.Equals(m.Groups["role"].Value, role, StringComparison.Ordinal))
            .GroupBy(m => m.Groups["table"].Value, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g.SelectMany(m => SplitColumns(m.Groups["columns"].Value)).ToList(),
                StringComparer.Ordinal);

    private static List<string> SplitColumns(string columnList) =>
        columnList.Split(',')
            .Select(c => c.Trim())
            .Where(c => c.Length > 0)
            .ToList();

    /// <summary>
    /// The one comparison the real assertions and the meta-test share: does this .sql text's viewer carve
    /// match <see cref="DarlingManagedRoles.ViewerRestrictedConfigTables"/> table-for-table, column-for-column?
    /// </summary>
    private static bool MatchesCSharpAcl(string sql)
    {
        var granted = ParseColumnGrants(sql, ViewerRole);
        if (granted.Count != DarlingManagedRoles.ViewerRestrictedConfigTables.Count)
        {
            return false;
        }

        foreach (var acl in DarlingManagedRoles.ViewerRestrictedConfigTables)
        {
            if (!granted.TryGetValue(acl.Table, out var sqlColumns) ||
                !sqlColumns.ToHashSet(StringComparer.Ordinal).SetEquals(acl.NonSecretColumns))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Rewrites one table's GRANT column list — the mutation engine for the meta-test. Removing a column is
    /// the exact shape of the #1639 drift; adding one is its mirror image.
    /// </summary>
    private static string MutateGrantColumns(
        string sql, string table, Func<IEnumerable<string>, IEnumerable<string>> mutate) =>
        ColumnGrant.Replace(sql, m =>
        {
            if (!string.Equals(m.Groups["table"].Value, table, StringComparison.Ordinal))
            {
                return m.Value;
            }

            var columns = mutate(SplitColumns(m.Groups["columns"].Value));
            return $"GRANT SELECT ({string.Join(", ", columns)}) ON {ConfigSchema}.{table} TO {m.Groups["role"].Value};";
        });
}
