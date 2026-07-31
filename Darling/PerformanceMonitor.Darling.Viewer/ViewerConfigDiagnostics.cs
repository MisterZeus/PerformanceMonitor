/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Npgsql;

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// The viewer's startup self-description (#1954): where darling.json came from, and what the viewer
/// actually parsed out of it — everything except credentials.
///
/// <para><b>Why this exists.</b> Field reports of "the viewer will not connect" were undiagnosable from the
/// log, because the viewer said neither which of its four candidate config paths it read nor what it got.
/// Two very different faults — reading the WRONG file, and reading the RIGHT file with a wrong value —
/// produced the identical generic failure. The certificate is the sharpest case: the documented
/// bring-your-own connection string carries a bare <c>Root Certificate=server.crt</c>, which Npgsql resolves
/// against the process WORKING DIRECTORY, not the viewer's install directory or the darling.json directory.
/// A viewer launched from a shortcut and one launched from a shell therefore look for the cert in different
/// places, and neither one used to say where.</para>
///
/// <para><b>Redaction is structural, not careful.</b> The summary is built from an ALLOWLIST of connection-string
/// properties (host, port, username, database, SSL mode, search path, root certificate) read off a parsed
/// <see cref="NpgsqlConnectionStringBuilder"/>. There is no path through this class that copies the caller's
/// connection string into its output, so no key an operator adds later — <c>Password</c>, <c>Passfile</c>, a
/// future credential keyword — can leak by being forgotten. A string that will not parse reports only the
/// exception TYPE for the same reason: an Npgsql parse message can quote the offending fragment.
/// <c>ViewerConfigDiagnosticsTests</c> pins this by feeding a live password through and asserting it never
/// appears in the output.</para>
/// </summary>
public static class ViewerConfigDiagnostics
{
    /// <summary>Label column width, so the block lines up when rendered in the log and the failure overlay.</summary>
    private const int LabelWidth = 20;

    /// <summary>The log source every diagnostic line is written under, so an operator can grep one tag.</summary>
    public const string LogSource = "Config";

    /// <summary>
    /// Which darling.json the viewer resolved, by which rule, and whether it is there. Emitted BEFORE the
    /// load attempt, so a file that does not exist or will not parse still tells the operator where the
    /// viewer looked.
    /// </summary>
    public static IReadOnlyList<string> DescribeConfigLocation(ViewerConfigLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);

        var lines = new List<string>
        {
            Line("darling.json source", SourceLabel(location.Source)),
            Line("darling.json path", location.FullPath),
        };

        /* Only worth a second line when the two differ — a relative DARLING_CONFIG or command-line path is
           precisely the case where the operator's mental path and the viewer's are not the same string. */
        if (!string.Equals(location.Path, location.FullPath, StringComparison.Ordinal))
        {
            lines.Add(Line("  as configured", location.Path));
        }

        lines.Add(Line("darling.json exists", location.Exists ? "yes" : "NO"));
        return lines;
    }

    /// <summary>
    /// The non-secret parse summary: what the viewer will actually connect with. <paramref name="managed"/>
    /// comes from the config rather than the connection string (in managed mode the string is derived, so
    /// <c>postgres.connectionString</c> in the file is not consulted at all — worth saying out loud).
    /// <paramref name="workingDirectory"/> is the directory a relative <c>Root Certificate</c> resolves
    /// against; null means the process working directory, which is what Npgsql itself would use (tests pass
    /// an explicit directory rather than mutating process state).
    /// </summary>
    public static IReadOnlyList<string> DescribeConnection(
        string? connectionString, bool managed, string? workingDirectory = null)
    {
        /* No connection string means the file was never loaded (missing, or it threw). Say only that —
           reporting a managed flag or a set of fields nothing was parsed from would be an invention. */
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return new[] { Line("connection", "(darling.json was not loaded — nothing was parsed)") };
        }

        var lines = new List<string>
        {
            Line(
                "postgres.managed",
                managed
                    ? "true (connection derived by the viewer; postgres.connectionString is not read)"
                    : "false (postgres.connectionString used verbatim)"),
        };

        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (Exception ex)
        {
            /* Type only, never the message: Npgsql's parse errors can quote the fragment they choked on,
               and that fragment can be the credential half of the string. */
            lines.Add(Line(
                "connection",
                $"COULD NOT BE PARSED ({ex.GetType().Name}) — check postgres.connectionString in darling.json"));
            return lines;
        }

        lines.Add(Line("Host", Present(builder.Host)));
        lines.Add(Line("Port", builder.Port.ToString(CultureInfo.InvariantCulture)));
        lines.Add(Line("Username", Present(builder.Username)));
        lines.Add(Line("Database", Present(builder.Database)));
        lines.Add(Line("SSL Mode", builder.SslMode.ToString()));
        lines.Add(Line("Search Path", Present(builder.SearchPath)));
        lines.AddRange(DescribeRootCertificate(builder.RootCertificate, workingDirectory));
        return lines;
    }

    /// <summary>
    /// The whole block, as it goes to the log and into the connection-failure overlay. Pass a null
    /// <paramref name="connectionString"/> for the pre-load failures (missing / unreadable file), where the
    /// location lines are the entire honest answer.
    /// </summary>
    public static string BuildDetails(
        ViewerConfigLocation location, string? connectionString, bool managed, string? workingDirectory = null)
    {
        var lines = new List<string>(DescribeConfigLocation(location));
        lines.AddRange(DescribeConnection(connectionString, managed, workingDirectory));
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// The certificate half: the value as written, the absolute path Npgsql will actually open, and whether
    /// that file is there. Verify-full against a cert that is not where the string says is one of the two
    /// failures this whole feature exists to name.
    /// </summary>
    private static IReadOnlyList<string> DescribeRootCertificate(string? rootCertificate, string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootCertificate))
        {
            return new[] { Line("Root Certificate", "(not set)") };
        }

        var lines = new List<string> { Line("Root Certificate", rootCertificate) };

        string resolved;
        try
        {
            /* Npgsql hands the path to the file system as given, so a relative one resolves against the
               process working directory — mirror that exactly rather than against the install directory. */
            resolved = workingDirectory is null
                ? Path.GetFullPath(rootCertificate)
                : Path.GetFullPath(rootCertificate, workingDirectory);
        }
        catch (Exception ex)
        {
            lines.Add(Line("  resolves to", $"(not a usable path: {ex.GetType().Name})"));
            return lines;
        }

        lines.Add(Line("  resolves to", resolved));
        if (!Path.IsPathRooted(rootCertificate))
        {
            lines.Add(Line("  relative to", workingDirectory ?? Environment.CurrentDirectory));
        }

        lines.Add(Line("  exists", File.Exists(resolved) ? "yes" : "NO"));
        return lines;
    }

    private static string SourceLabel(ViewerConfigSource source) => source switch
    {
        ViewerConfigSource.CommandLine => "command-line argument (outranks DARLING_CONFIG)",
        ViewerConfigSource.EnvironmentVariable => "DARLING_CONFIG environment variable",
        ViewerConfigSource.BesideViewer => "beside the viewer executable",
        ViewerConfigSource.ServiceRoot => "the service root, one level above the viewer",
        _ => source.ToString(),
    };

    private static string Line(string label, string value) =>
        string.Concat((label + ":").PadRight(LabelWidth), " ", value);

    private static string Present(string? value) => string.IsNullOrWhiteSpace(value) ? "(not set)" : value;
}
