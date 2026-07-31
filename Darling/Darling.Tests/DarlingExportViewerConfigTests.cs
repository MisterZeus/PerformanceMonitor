/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PerformanceMonitor.Darling.Service;
using PerformanceMonitor.Darling.Viewer;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// The <c>--export-viewer-config</c> verb (#1953): the service writes the viewer machine's WHOLE handoff
/// folder instead of making an operator hand-merge <c>--print-viewer-connection</c>'s terminal output into
/// JSON copied out of the docs — the field report that motivated this also had to discover by trial that the
/// VIEWER's darling.json wants <c>"managed": false</c> while the SERVER runs <c>true</c>.
/// <para>The load-bearing test here is the SEAM one: the exported (comment-carrying) JSON is fed to the real
/// <see cref="ViewerSettings"/> parser — the code the Viewer actually runs — and must yield the exact
/// connection string. Asserting the file merely "looks right" would pass with a managed:true export, a
/// comment syntax the parser rejects, or a mis-escaped string, all of which break only on the viewer machine.</para>
/// </summary>
public sealed class DarlingExportViewerConfigTests
{
    private const string Pem = "-----BEGIN CERTIFICATE-----\nMIIBTESTCERTPEM\n-----END CERTIFICATE-----";

    [Theory]
    [InlineData("--export-viewer-config", true)]
    [InlineData("--EXPORT-VIEWER-CONFIG", true)]
    [InlineData("--print-viewer-connection", false)]
    [InlineData("--validate-config", false)]
    [InlineData("--nonsense", false)]
    public void IsExportViewerConfigVerb_RecognizesTheVerb_CaseInsensitive(string arg, bool expected)
    {
        Assert.Equal(expected, DarlingCliCommands.IsExportViewerConfigVerb(arg));
    }

    /// <summary>The #1950 reachability pin covers this generically; naming it here makes the intent local:
    /// an unreachable verb is dispatch code the #1581 classifier bounces as "Unknown option".</summary>
    [Fact]
    public void IsKnownVerb_ReachesTheExportVerb()
    {
        Assert.True(DarlingCliCommands.IsKnownVerb("--export-viewer-config"));
        Assert.Equal(StartupAction.RunKnownVerb, DarlingCliCommands.ClassifyStartupArgs(["--export-viewer-config"]));
    }

    [Fact]
    public void UsageText_ListsTheExportVerb()
    {
        Assert.Contains("--export-viewer-config", DarlingCliCommands.UsageText(), StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveViewerExportDirectory_NoArgument_LandsBesideTheServiceConfig()
    {
        var resolved = DarlingCliCommands.ResolveViewerExportDirectory(@"C:\Darling\darling.json", null);
        Assert.Equal(Path.Combine(@"C:\Darling", "viewer-config"), resolved);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveViewerExportDirectory_BlankArgument_FallsBackToTheDefault(string outputDirectory)
    {
        var resolved = DarlingCliCommands.ResolveViewerExportDirectory(@"C:\Darling\darling.json", outputDirectory);
        Assert.Equal(Path.Combine(@"C:\Darling", "viewer-config"), resolved);
    }

    [Fact]
    public void ResolveViewerExportDirectory_OperatorNamedDirectory_WinsAndIsAbsolute()
    {
        var resolved = DarlingCliCommands.ResolveViewerExportDirectory(@"C:\Darling\darling.json", @"  D:\handoff  ");
        Assert.Equal(@"D:\handoff", resolved);

        /* A relative destination is anchored to the CWD, not silently left relative — the operator gets a
           path they can copy from. */
        Assert.Equal(
            Path.GetFullPath("out-here"),
            DarlingCliCommands.ResolveViewerExportDirectory(@"C:\Darling\darling.json", "out-here"));
    }

    /// <summary>
    /// THE SEAM: the exported JSON is parsed by the Viewer's own <see cref="ViewerSettings"/> — comments and
    /// all — and must hand back the connection string byte-for-byte. This is what makes the export "ready to
    /// drop on the viewer machine" a fact rather than a claim.
    /// </summary>
    [Fact]
    public void BuildViewerConfigJson_IsParsedByTheRealViewerParser_YieldingTheStringVerbatim()
    {
        var connectionString = DarlingCliCommands.BuildViewerConnectionString(
            "192.168.1.205", 5641, "viewer", "s3cretPW", DarlingCliCommands.ViewerClientCertificateFileName);

        var json = DarlingCliCommands.BuildViewerConfigJson(connectionString, DateTimeOffset.UnixEpoch);

        Assert.Equal(connectionString, ViewerSettings.Parse(json).ConnectionString);
    }

    /// <summary>
    /// managed:false is the whole reason this verb exists — the reporter discovered the flip by trial. A
    /// managed:true export sends the Viewer looking for a bundled local PostgreSQL, so pin BOTH the literal
    /// and the behavior (the managed derivation would throw here: there is no credential file).
    /// </summary>
    [Fact]
    public void BuildViewerConfigJson_SetsManagedFalse_AndExplainsWhyItDiffersFromTheServer()
    {
        var json = DarlingCliCommands.BuildViewerConfigJson("Host=h;Database=darling", DateTimeOffset.UnixEpoch);

        Assert.Contains("\"managed\": false", json, StringComparison.Ordinal);
        Assert.Contains("who OWNS the PostgreSQL", json, StringComparison.Ordinal);

        /* Behavioral half: the parser takes the BYO branch (verbatim string), never the managed derivation. */
        Assert.Equal("Host=h;Database=darling", ViewerSettings.Parse(json).ConnectionString);
    }

    /// <summary>#1953 item 4: the exported file documents its own fields, including what may legally follow
    /// <c>Root Certificate=</c> — the thing the docs failed to state and the reporter had to guess.</summary>
    [Fact]
    public void BuildViewerConfigJson_DocumentsEveryFieldAndTheRootCertificateValues()
    {
        var json = DarlingCliCommands.BuildViewerConfigJson("Host=h;Database=darling", DateTimeOffset.UnixEpoch);

        foreach (var field in new[] { "Host", "Port", "Username", "Password", "Database", "Search Path", "SSL Mode", "Root Certificate" })
        {
            Assert.Contains(field, json, StringComparison.Ordinal);
        }

        /* Both legal Root Certificate forms, and the trap: a bare name resolves against the WORKING directory.
           Asserted as the whole phrase — a line wrap that splits it is a documentation defect, not a formatting
           detail, because the reader scanning for the caveat is scanning for those two words together. */
        Assert.Contains("WORKING DIRECTORY", json, StringComparison.Ordinal);
        Assert.Contains("an absolute path", json, StringComparison.Ordinal);
        Assert.Contains(@"C:\Darling\server.crt", json, StringComparison.Ordinal);

        /* And why VerifyFull must not be "helpfully" downgraded to Require (it silently voids the pin). */
        Assert.Contains("Require", json, StringComparison.Ordinal);

        /* ASCII only, same as the README: this lands on a machine with an unknown console code page. */
        Assert.All(json, c => Assert.True(c < 128, $"non-ASCII character '{c}' in the exported darling.json"));
    }

    /// <summary>
    /// The instruction the export leads with has to be the one that WORKS unedited. A bare
    /// <c>Root Certificate=server.crt</c> resolves against the Viewer's working directory, so
    /// "copy the files beside the Viewer executable" works as-is while "put the folder anywhere and point
    /// DARLING_CONFIG at it" needs the certificate path made absolute — the export must not promise the
    /// second without that condition (#1970 is the viewer-side fix that would remove the condition).
    /// </summary>
    [Fact]
    public void ExportedDocs_LeadWithTheInstructionThatWorksUnedited_AndQualifyTheOtherOne()
    {
        var json = DarlingCliCommands.BuildViewerConfigJson("Host=h;Database=darling", DateTimeOffset.UnixEpoch);
        var readme = DarlingCliCommands.BuildViewerConfigReadme("h", 5641, "viewer", DateTimeOffset.UnixEpoch);

        foreach (var text in new[] { json, readme })
        {
            /* The FIRST mention of either phrasing — both texts also repeat "beside the Viewer executable"
               further down, in the Root Certificate reference, and it is the lead instruction being pinned. */
            var besideTheExe = new[] { "beside the Viewer executable", "next to the Viewer executable" }
                .Select(phrase => text.IndexOf(phrase, StringComparison.Ordinal))
                .Where(at => at >= 0)
                .DefaultIfEmpty(-1)
                .Min();
            var darlingConfig = text.IndexOf("DARLING_CONFIG", StringComparison.Ordinal);

            Assert.True(besideTheExe >= 0, "the beside-the-executable placement is not documented");
            Assert.True(darlingConfig >= 0, "the DARLING_CONFIG placement is not documented");
            Assert.True(
                besideTheExe < darlingConfig,
                "DARLING_CONFIG is offered before the placement that works unedited");
        }

        /* And the DARLING_CONFIG route names its condition rather than implying "nothing to edit"
           (case-insensitive — the JSON shouts FULL, the README does not; the condition is what matters). */
        Assert.Contains("full path", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("full path", readme, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The README is the same reference for an operator who never opens JSON — and it must NOT be a
    /// second copy of the secret: the credential lives in exactly one exported file.</summary>
    [Fact]
    public void BuildViewerConfigReadme_DocumentsTheHandoff_WithoutCarryingThePassword()
    {
        var readme = DarlingCliCommands.BuildViewerConfigReadme("192.168.1.205", 5641, "viewer", DateTimeOffset.UnixEpoch);

        Assert.Contains("DARLING_CONFIG", readme, StringComparison.Ordinal);
        Assert.Contains("Host=192.168.1.205", readme, StringComparison.Ordinal);
        Assert.Contains("Username=viewer", readme, StringComparison.Ordinal);
        Assert.Contains("managed = false", readme, StringComparison.Ordinal);
        Assert.Contains("Root Certificate=server.crt", readme, StringComparison.Ordinal);
        Assert.Contains("WORKING DIRECTORY", readme, StringComparison.Ordinal);
        Assert.Contains("absolute path", readme, StringComparison.Ordinal);

        /* ASCII only: this file gets opened in Notepad on a machine with an unknown code page. */
        Assert.All(readme, c => Assert.True(c < 128, $"non-ASCII character '{c}' in README.txt"));
    }

    [Fact]
    public async Task ExportViewerConfigAsync_ByoMode_RefusesAndWritesNothing()
    {
        var root = Directory.CreateTempSubdirectory("darling-export-byo-");
        try
        {
            var configPath = Path.Combine(root.FullName, "darling.json");
            await File.WriteAllTextAsync(configPath,
                """{ "postgres": { "connectionString": "Host=localhost;Database=darling" } }""");

            var output = new StringWriter();
            var error = new StringWriter();
            var exit = await DarlingCliCommands.ExportViewerConfigAsync(
                configPath, null, output, error, CancellationToken.None);

            Assert.Equal(1, exit);
            Assert.Contains("--export-viewer-config", error.ToString(), StringComparison.Ordinal);
            Assert.Contains("bring-your-own", error.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Equal("", output.ToString());
            Assert.False(Directory.Exists(Path.Combine(root.FullName, "viewer-config")));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>A store that has never been network-exposed has no credential yet. The verb must name the
    /// missing thing and the one action that produces it — this is the error path an operator hits first.</summary>
    [Fact]
    public async Task ExportViewerConfigAsync_MissingCredential_NamesTheFileAndTheFix()
    {
        var root = Directory.CreateTempSubdirectory("darling-export-nocred-");
        try
        {
            var dataDirectory = Path.Combine(root.FullName, "pg");
            var configPath = Path.Combine(root.FullName, "darling.json");
            await File.WriteAllTextAsync(configPath, ManagedConfigJson(dataDirectory));

            var output = new StringWriter();
            var error = new StringWriter();
            var exit = await DarlingCliCommands.ExportViewerConfigAsync(
                configPath, null, output, error, CancellationToken.None);
            var stderr = error.ToString();

            Assert.Equal(1, exit);
            Assert.Contains("pg-viewer-credential.dpapi", stderr, StringComparison.Ordinal);
            Assert.Contains("Start the PerformanceMonitor", stderr, StringComparison.Ordinal);
            Assert.Equal("", output.ToString());
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>
    /// The cert is REQUIRED for the export (unlike the print verb, which still has a useful string without
    /// it): the exported connection is SSL Mode=VerifyFull, so a folder without the PEM is a handoff that
    /// cannot connect — precisely the half-finished setup this verb exists to eliminate.
    /// </summary>
    [Fact]
    public async Task ExportViewerConfigAsync_MissingCertificate_RefusesRatherThanExportingSomethingBroken()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI requires Windows.");

        var root = Directory.CreateTempSubdirectory("darling-export-nocert-");
        try
        {
            var dataDirectory = Path.Combine(root.FullName, "pg");
            File.WriteAllText(
                DarlingManagedPostgres.ViewerCredentialPathFor(dataDirectory),
                DarlingSecrets.Protect("viewer-secret-pw"));

            var configPath = Path.Combine(root.FullName, "darling.json");
            await File.WriteAllTextAsync(configPath, ManagedConfigJson(dataDirectory));

            var output = new StringWriter();
            var error = new StringWriter();
            var exit = await DarlingCliCommands.ExportViewerConfigAsync(
                configPath, null, output, error, CancellationToken.None);
            var stderr = error.ToString();

            Assert.Equal(1, exit);
            Assert.Contains("server.crt", stderr, StringComparison.Ordinal);
            Assert.Contains("VerifyFull", stderr, StringComparison.Ordinal);
            Assert.Equal("", output.ToString());
            Assert.False(Directory.Exists(Path.Combine(root.FullName, "viewer-config")));

            /* The refusal must never leak the credential it just decrypted. */
            Assert.DoesNotContain("viewer-secret-pw", stderr, StringComparison.Ordinal);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>
    /// End-to-end on the managed layout: the three files land in the default folder, the JSON is consumed by
    /// the REAL viewer parser and dials the exposed endpoint as the configured role, the cert is copied
    /// verbatim, and the live-credential warning names the file it is about to write.
    /// </summary>
    [Fact]
    public async Task ExportViewerConfigAsync_ManagedViewer_WritesAFolderTheViewerParserAccepts()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI requires Windows.");

        var root = Directory.CreateTempSubdirectory("darling-export-");
        try
        {
            var dataDirectory = Path.Combine(root.FullName, "pg");
            var credentialPath = DarlingManagedPostgres.ViewerCredentialPathFor(dataDirectory);
            File.WriteAllText(credentialPath, DarlingSecrets.Protect("viewer-secret-pw"));
            File.WriteAllText(
                Path.Combine(Path.GetDirectoryName(credentialPath)!, DarlingManagedPostgres.ServerCertFileName),
                Pem);

            var configPath = Path.Combine(root.FullName, "darling.json");
            await File.WriteAllTextAsync(configPath, ManagedConfigJson(dataDirectory));

            var output = new StringWriter();
            var error = new StringWriter();
            var exit = await DarlingCliCommands.ExportViewerConfigAsync(
                configPath, null, output, error, CancellationToken.None);

            Assert.Equal(0, exit);

            var exported = Path.Combine(root.FullName, "viewer-config");
            var exportedConfig = Path.Combine(exported, "darling.json");
            var exportedCert = Path.Combine(exported, "server.crt");
            var exportedReadme = Path.Combine(exported, "README.txt");
            Assert.True(File.Exists(exportedConfig));
            Assert.True(File.Exists(exportedCert));
            Assert.True(File.Exists(exportedReadme));

            /* The seam again, this time on the file as WRITTEN (encoding, escaping, comments and all). */
            var settings = ViewerSettings.Parse(await File.ReadAllTextAsync(exportedConfig));
            var expected = DarlingCliCommands.BuildViewerConnectionString(
                "192.168.1.205", 5641, "viewer", "viewer-secret-pw", "server.crt");
            Assert.Equal(expected, settings.ConnectionString);

            /* The cert travels byte-for-byte: VerifyFull pins THIS PEM. */
            Assert.Contains(Pem.Trim(), (await File.ReadAllTextAsync(exportedCert)).Trim(), StringComparison.Ordinal);

            /* STDOUT is the machine-readable manifest; STDERR carries the loud secret warning, naming the file. */
            var stdout = output.ToString();
            Assert.Contains(exportedConfig, stdout, StringComparison.Ordinal);
            Assert.Contains(exportedCert, stdout, StringComparison.Ordinal);
            Assert.Contains(exportedReadme, stdout, StringComparison.Ordinal);

            var stderr = error.ToString();
            Assert.Contains("LIVE database password", stderr, StringComparison.Ordinal);
            Assert.Contains(exportedConfig, stderr, StringComparison.Ordinal);
            Assert.Contains("DARLING_CONFIG", stderr, StringComparison.Ordinal);

            /* The password value itself is never echoed to either stream — only written into the one file. */
            Assert.DoesNotContain("viewer-secret-pw", stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("viewer-secret-pw", stderr, StringComparison.Ordinal);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ExportViewerConfigAsync_OperatorNamedDirectory_IsUsedAndCreated()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI requires Windows.");

        var root = Directory.CreateTempSubdirectory("darling-export-named-");
        try
        {
            var dataDirectory = Path.Combine(root.FullName, "pg");
            var credentialPath = DarlingManagedPostgres.ViewerCredentialPathFor(dataDirectory);
            File.WriteAllText(credentialPath, DarlingSecrets.Protect("viewer-secret-pw"));
            File.WriteAllText(
                Path.Combine(Path.GetDirectoryName(credentialPath)!, DarlingManagedPostgres.ServerCertFileName),
                Pem);

            var configPath = Path.Combine(root.FullName, "darling.json");
            await File.WriteAllTextAsync(configPath, ManagedConfigJson(dataDirectory));

            /* A directory that does not exist yet — the operator names where they want it, not where we do. */
            var destination = Path.Combine(root.FullName, "handoff", "laptop");

            var exit = await DarlingCliCommands.ExportViewerConfigAsync(
                configPath, destination, new StringWriter(), new StringWriter(), CancellationToken.None);

            Assert.Equal(0, exit);
            Assert.True(File.Exists(Path.Combine(destination, "darling.json")));
            Assert.True(File.Exists(Path.Combine(destination, "server.crt")));
            Assert.True(File.Exists(Path.Combine(destination, "README.txt")));
            Assert.False(Directory.Exists(Path.Combine(root.FullName, "viewer-config")));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>
    /// This verb's positional argument is the DESTINATION, while every sibling verb's is a config path — so
    /// the predictable operator error is typing darling.json there. It must be named as such, not surface as
    /// a directory-create failure against an existing file.
    /// </summary>
    [Fact]
    public async Task ExportViewerConfigAsync_DestinationLooksLikeAConfigFile_SaysSoAndPointsAtTheFlag()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI requires Windows.");

        var root = Directory.CreateTempSubdirectory("darling-export-miscue-");
        try
        {
            /* As above: the argument-shape guard precedes the config load, so no DPAPI material is needed. */
            var dataDirectory = Path.Combine(root.FullName, "pg");
            var configPath = Path.Combine(root.FullName, "darling.json");
            await File.WriteAllTextAsync(configPath, ManagedConfigJson(dataDirectory));

            var output = new StringWriter();
            var error = new StringWriter();

            /* The sibling-verb muscle memory: --export-viewer-config <the config path>. */
            var exit = await DarlingCliCommands.ExportViewerConfigAsync(
                null, configPath, output, error, CancellationToken.None);
            var stderr = error.ToString();

            Assert.Equal(1, exit);
            Assert.Contains("is a file, not a directory", stderr, StringComparison.Ordinal);
            Assert.Contains("--config", stderr, StringComparison.Ordinal);
            Assert.Equal("", output.ToString());

            /* And the config it was pointed at is untouched — no export written over it. */
            Assert.Equal(ManagedConfigJson(dataDirectory), await File.ReadAllTextAsync(configPath));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>
    /// The unrecoverable one. Exporting into the SERVICE's own config directory would overwrite its
    /// darling.json with the viewer's — destroying every monitored server, every DPAPI encryptedPassword and
    /// the MCP/web tokens, none of which exist anywhere else — and it is one keystroke from a legitimate
    /// command, since the install directory is the obvious place to put a handoff folder. Reproduced before
    /// the guard existed: a 390-byte service config became the 2813-byte viewer config, exit 0, no warning.
    /// </summary>
    [Fact]
    public async Task ExportViewerConfigAsync_DestinationIsTheServiceConfigDirectory_RefusesAndLeavesItIntact()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI requires Windows.");

        var root = Directory.CreateTempSubdirectory("darling-export-selfdestruct-");
        try
        {
            /* No credential or certificate is laid down: this guard runs BEFORE the config is even loaded,
               which is the point — a destination that would destroy the service's config is refused without
               decrypting anything. */
            var dataDirectory = Path.Combine(root.FullName, "pg");
            var configPath = Path.Combine(root.FullName, "darling.json");
            var original = ManagedConfigJson(dataDirectory);
            await File.WriteAllTextAsync(configPath, original);

            var output = new StringWriter();
            var error = new StringWriter();

            /* The destination is the directory the service's own darling.json lives in. */
            var exit = await DarlingCliCommands.ExportViewerConfigAsync(
                configPath, root.FullName, output, error, CancellationToken.None);

            Assert.Equal(1, exit);
            Assert.Contains("SERVICE's own", error.ToString(), StringComparison.Ordinal);
            Assert.Equal("", output.ToString());

            /* The whole point: the service config is byte-for-byte what it was. */
            Assert.Equal(original, await File.ReadAllTextAsync(configPath));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>
    /// A darling.json in the destination that this verb did not write is someone else's file — an operator's
    /// real config, or one pre-created by a local attacker who would keep OWNERSHIP of it through the harden
    /// (a Windows owner retains WRITE_DAC). Stop rather than write a live password into it.
    /// </summary>
    [Fact]
    public async Task ExportViewerConfigAsync_DestinationHoldsAForeignDarlingJson_RefusesToOverwriteIt()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI requires Windows.");

        var root = Directory.CreateTempSubdirectory("darling-export-foreign-");
        try
        {
            /* As above: refused before the config load, so no credential material is needed to reach it. */
            var dataDirectory = Path.Combine(root.FullName, "pg");
            var configPath = Path.Combine(root.FullName, "darling.json");
            await File.WriteAllTextAsync(configPath, ManagedConfigJson(dataDirectory));

            var destination = Path.Combine(root.FullName, "handoff");
            Directory.CreateDirectory(destination);
            const string foreign = """{ "postgres": { "managed": true }, "servers": [ { "name": "MINE" } ] }""";
            await File.WriteAllTextAsync(Path.Combine(destination, "darling.json"), foreign);

            var output = new StringWriter();
            var error = new StringWriter();
            var exit = await DarlingCliCommands.ExportViewerConfigAsync(
                configPath, destination, output, error, CancellationToken.None);

            Assert.Equal(1, exit);
            Assert.Contains("not written by --export-viewer-config", error.ToString(), StringComparison.Ordinal);
            Assert.Equal(foreign, await File.ReadAllTextAsync(Path.Combine(destination, "darling.json")));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>Re-exporting after a credential or certificate rotation is a documented workflow, so the
    /// verb's OWN previous output is replaced without ceremony — the guard above must not break that.</summary>
    [Fact]
    public async Task ExportViewerConfigAsync_RerunOverItsOwnOutput_OverwritesSilently()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI requires Windows.");

        var root = Directory.CreateTempSubdirectory("darling-export-rerun-");
        try
        {
            var dataDirectory = Path.Combine(root.FullName, "pg");
            var credentialPath = DarlingManagedPostgres.ViewerCredentialPathFor(dataDirectory);
            File.WriteAllText(credentialPath, DarlingSecrets.Protect("viewer-secret-pw"));
            var certPath = Path.Combine(Path.GetDirectoryName(credentialPath)!, DarlingManagedPostgres.ServerCertFileName);
            File.WriteAllText(certPath, Pem);

            var configPath = Path.Combine(root.FullName, "darling.json");
            await File.WriteAllTextAsync(configPath, ManagedConfigJson(dataDirectory));

            var first = await DarlingCliCommands.ExportViewerConfigAsync(
                configPath, null, new StringWriter(), new StringWriter(), CancellationToken.None);
            Assert.Equal(0, first);

            /* BOTH rotate — the docs say to re-run after a credential OR certificate rotation, and rotating
               both is what makes the re-export load-bearing: an implementation that saw its own marker and
               SKIPPED the rewrite would still satisfy a marker-only assertion, because the marker is already
               in the file it left alone. */
            const string rotated = "-----BEGIN CERTIFICATE-----\nMIIBROTATEDPEM\n-----END CERTIFICATE-----";
            File.WriteAllText(certPath, rotated);
            File.WriteAllText(credentialPath, DarlingSecrets.Protect("rotated-pw"));

            var error = new StringWriter();
            var second = await DarlingCliCommands.ExportViewerConfigAsync(
                configPath, null, new StringWriter(), error, CancellationToken.None);

            Assert.Equal(0, second);
            Assert.DoesNotContain("Refusing", error.ToString(), StringComparison.Ordinal);

            var exported = Path.Combine(root.FullName, "viewer-config");
            Assert.Contains(rotated, await File.ReadAllTextAsync(Path.Combine(exported, "server.crt")), StringComparison.Ordinal);

            var rewritten = await File.ReadAllTextAsync(Path.Combine(exported, "darling.json"));
            Assert.Contains("Password=rotated-pw", rewritten, StringComparison.Ordinal);
            Assert.DoesNotContain("viewer-secret-pw", rewritten, StringComparison.Ordinal);
            Assert.Contains(DarlingCliCommands.ViewerConfigMarker, rewritten, StringComparison.Ordinal);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>
    /// A junction needs no privilege to create on Windows, which makes it the way an unprivileged local user
    /// redirects the cleartext credential into a directory they control. Refused by inspecting the
    /// destination's reparse-point attribute, so this pins the one destination guard whose failure mode is
    /// silent — a wrong path or a short-circuit would simply export through the junction.
    /// </summary>
    [Fact]
    public async Task ExportViewerConfigAsync_DestinationIsAJunction_Refuses()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Junctions are a Windows concept.");

        var root = Directory.CreateTempSubdirectory("darling-export-junction-");
        try
        {
            var dataDirectory = Path.Combine(root.FullName, "pg");
            var credentialPath = DarlingManagedPostgres.ViewerCredentialPathFor(dataDirectory);
            File.WriteAllText(credentialPath, DarlingSecrets.Protect("viewer-secret-pw"));
            File.WriteAllText(
                Path.Combine(Path.GetDirectoryName(credentialPath)!, DarlingManagedPostgres.ServerCertFileName),
                Pem);

            var configPath = Path.Combine(root.FullName, "darling.json");
            await File.WriteAllTextAsync(configPath, ManagedConfigJson(dataDirectory));

            /* The attacker-controlled real destination, and the junction pointing at it. */
            var elsewhere = Path.Combine(root.FullName, "elsewhere");
            Directory.CreateDirectory(elsewhere);
            var junction = Path.Combine(root.FullName, "handoff");

            using (var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{junction}\" \"{elsewhere}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!)
            {
                await mklink.WaitForExitAsync();
                Assert.SkipUnless(mklink.ExitCode == 0, "could not create a junction on this machine");
            }

            var output = new StringWriter();
            var error = new StringWriter();
            var exit = await DarlingCliCommands.ExportViewerConfigAsync(
                configPath, junction, output, error, CancellationToken.None);

            Assert.Equal(1, exit);
            Assert.Contains("junction or symbolic link", error.ToString(), StringComparison.Ordinal);
            Assert.Equal("", output.ToString());

            /* Nothing reached the real target the junction pointed at. */
            Assert.Empty(Directory.GetFiles(elsewhere));

            /* Remove the junction itself (a non-recursive delete unlinks it without touching the target)
               before the recursive cleanup below, which cannot walk one. */
            Directory.Delete(junction);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>The exported secret must not be left readable by ordinary users — the file is CLEARTEXT, so
    /// read access is the credential with no further step.</summary>
    [Fact]
    public async Task ExportViewerConfigAsync_HardensTheExportedSecret_NotReadableByOrdinaryUsers()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "ACLs require Windows.");

        var root = Directory.CreateTempSubdirectory("darling-export-acl-");
        try
        {
            var dataDirectory = Path.Combine(root.FullName, "pg");
            var credentialPath = DarlingManagedPostgres.ViewerCredentialPathFor(dataDirectory);
            File.WriteAllText(credentialPath, DarlingSecrets.Protect("viewer-secret-pw"));
            File.WriteAllText(
                Path.Combine(Path.GetDirectoryName(credentialPath)!, DarlingManagedPostgres.ServerCertFileName),
                Pem);

            var configPath = Path.Combine(root.FullName, "darling.json");
            await File.WriteAllTextAsync(configPath, ManagedConfigJson(dataDirectory));

            var exit = await DarlingCliCommands.ExportViewerConfigAsync(
                configPath, null, new StringWriter(), new StringWriter(), CancellationToken.None);

            /* Exit 0 is itself the claim that the secret is protected — the verb returns non-zero when it is
               not, so this assertion and the ACL check below pin the same contract from both sides. */
            Assert.Equal(0, exit);
            Assert.False(DarlingFileSecurity.IsReadableByOrdinaryUsers(
                Path.Combine(root.FullName, "viewer-config", "darling.json")));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /* ---- the strict argument parser (pure) ---- */

    [Fact]
    public void TryParseExportViewerConfigArgs_NoArguments_TakesTheDefaults()
    {
        Assert.True(DarlingCliCommands.TryParseExportViewerConfigArgs([], out var config, out var directory, out var problem));
        Assert.Null(config);
        Assert.Null(directory);
        Assert.Null(problem);
    }

    [Fact]
    public void TryParseExportViewerConfigArgs_DestinationAndConfig_InEitherOrder()
    {
        Assert.True(DarlingCliCommands.TryParseExportViewerConfigArgs(
            [@"D:\handoff", "--config", @"C:\Darling\darling.json"], out var config, out var directory, out _));
        Assert.Equal(@"C:\Darling\darling.json", config);
        Assert.Equal(@"D:\handoff", directory);

        Assert.True(DarlingCliCommands.TryParseExportViewerConfigArgs(
            ["--config", @"C:\Darling\darling.json", @"D:\handoff"], out config, out directory, out _));
        Assert.Equal(@"C:\Darling\darling.json", config);
        Assert.Equal(@"D:\handoff", directory);
    }

    /// <summary>
    /// The reason this parser is strict rather than last-wins like its siblings: every one of these used to
    /// be silently accepted as the DESTINATION, and a destination nobody chose is where a live cleartext
    /// password would have landed — for a bare <c>--config</c>, a folder literally named "--config" under the
    /// working directory, which for the elevated prompt the docs prescribe is C:\Windows\System32.
    /// </summary>
    [Theory]
    [InlineData(new[] { "--config" }, "--config needs a path")]
    [InlineData(new[] { "--force" }, "Unknown option")]
    [InlineData(new[] { "-o", "out" }, "Unknown option")]
    [InlineData(new[] { @"D:\one", @"D:\two" }, "takes ONE destination directory")]
    [InlineData(new[] { @"D:\one", "--config", @"C:\c.json", @"D:\two" }, "takes ONE destination directory")]
    public void TryParseExportViewerConfigArgs_Rejects_WithoutGuessingADestination(string[] rest, string expected)
    {
        Assert.False(DarlingCliCommands.TryParseExportViewerConfigArgs(rest, out var config, out var directory, out var problem));
        Assert.Contains(expected, problem, StringComparison.Ordinal);

        /* A rejected command must not hand the caller a half-parsed destination to write into. */
        _ = config;
        Assert.True(directory is null || rest.Contains(directory, StringComparer.Ordinal));
    }

    private static string ManagedConfigJson(string dataDirectory) =>
        $$"""
        {
          "postgres": {
            "managed": true,
            "port": 5641,
            "dataDirectory": {{JsonSerializer.Serialize(dataDirectory)}},
            "network": { "listen": "192.168.1.205", "allowFrom": "192.168.1.0/24", "role": "viewer" }
          }
        }
        """;
}

/// <summary>
/// #1953 item 3 — <c>--print-viewer-connection</c> must warn BEFORE it prints. The field report watched a live
/// password scroll into their terminal and only THEN read the advice to redirect it, which is a warning
/// arriving after the damage. Two separate <see cref="StringWriter"/>s structurally cannot see this (each
/// stream looks correct in isolation), so these tests write both streams into ONE buffer and assert the
/// interleaving — the same property the built binary is checked for on a real console.
/// </summary>
public sealed class DarlingPrintViewerConnectionOrderingTests
{
    [Fact]
    public async Task PrintViewerConnectionAsync_EmitsEveryWarningBeforeAnyPayloadReachesStdout()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI requires Windows.");

        var root = Directory.CreateTempSubdirectory("darling-printconn-order-");
        try
        {
            var dataDirectory = Path.Combine(root.FullName, "pg");
            var credentialPath = DarlingManagedPostgres.ViewerCredentialPathFor(dataDirectory);
            File.WriteAllText(credentialPath, DarlingSecrets.Protect("viewer-secret-pw"));
            File.WriteAllText(
                Path.Combine(Path.GetDirectoryName(credentialPath)!, DarlingManagedPostgres.ServerCertFileName),
                "-----BEGIN CERTIFICATE-----\nMIIBTESTCERTPEM\n-----END CERTIFICATE-----");

            var configPath = Path.Combine(root.FullName, "darling.json");
            await File.WriteAllTextAsync(configPath,
                $$"""
                {
                  "postgres": {
                    "managed": true,
                    "port": 5641,
                    "dataDirectory": {{JsonSerializer.Serialize(dataDirectory)}},
                    "network": { "listen": "192.168.1.205", "allowFrom": "192.168.1.0/24", "role": "viewer" }
                  }
                }
                """);

            var console = new InterleavedConsole();
            var exit = await DarlingCliCommands.PrintViewerConnectionAsync(
                configPath, console.Out, console.Error, CancellationToken.None);

            Assert.Equal(0, exit);

            var log = console.Log;
            var firstStdout = log.IndexOf("OUT:", StringComparison.Ordinal);
            var lastStderr = log.LastIndexOf("ERR:", StringComparison.Ordinal);

            Assert.True(firstStdout >= 0, "the verb printed nothing to STDOUT");
            Assert.True(lastStderr >= 0, "the verb warned about nothing");
            Assert.True(
                lastStderr < firstStdout,
                "a STDERR line was written AFTER the STDOUT payload — the secret hits scrollback before its " +
                $"warning (#1953 item 3). Interleaved log:{Environment.NewLine}{log}");

            /* And the warning is genuinely the guidance, not an empty line that happens to sort first. */
            var warningAt = log.IndexOf("LIVE database password", StringComparison.Ordinal);
            Assert.True(warningAt >= 0 && warningAt < firstStdout);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>The missing-cert NOTE used to be written after the payload — the one STDERR line that could
    /// still land behind the secret even after the warning block moved up.</summary>
    [Fact]
    public async Task PrintViewerConnectionAsync_MissingCertificateNote_AlsoPrecedesTheStdoutPayload()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI requires Windows.");

        var root = Directory.CreateTempSubdirectory("darling-printconn-order-nocert-");
        try
        {
            var dataDirectory = Path.Combine(root.FullName, "pg");
            File.WriteAllText(
                DarlingManagedPostgres.ViewerCredentialPathFor(dataDirectory),
                DarlingSecrets.Protect("viewer-secret-pw"));

            var configPath = Path.Combine(root.FullName, "darling.json");
            await File.WriteAllTextAsync(configPath,
                $$"""
                {
                  "postgres": {
                    "managed": true,
                    "port": 5641,
                    "dataDirectory": {{JsonSerializer.Serialize(dataDirectory)}},
                    "network": { "listen": "192.168.1.205", "allowFrom": "192.168.1.0/24", "role": "viewer" }
                  }
                }
                """);

            var console = new InterleavedConsole();
            var exit = await DarlingCliCommands.PrintViewerConnectionAsync(
                configPath, console.Out, console.Error, CancellationToken.None);

            Assert.Equal(0, exit);

            var log = console.Log;
            var note = log.IndexOf("does not exist yet", StringComparison.Ordinal);
            var firstStdout = log.IndexOf("OUT:", StringComparison.Ordinal);
            Assert.True(note >= 0, $"the missing-cert NOTE was not printed. Log:{Environment.NewLine}{log}");
            Assert.True(note < firstStdout, $"the NOTE trailed the payload. Log:{Environment.NewLine}{log}");
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Two <see cref="TextWriter"/>s over ONE buffer, each line tagged with the stream that wrote it — so a
    /// test can assert the ORDER of stdout vs stderr writes, which separate writers cannot show. Every
    /// TextWriter overload funnels through <see cref="Write(char)"/>, so this captures whatever the verb calls.
    /// </summary>
    private sealed class InterleavedConsole
    {
        private readonly StringBuilder _log = new();

        public TextWriter Out { get; }

        public TextWriter Error { get; }

        public InterleavedConsole()
        {
            Out = new TaggedWriter(_log, "OUT");
            Error = new TaggedWriter(_log, "ERR");
        }

        public string Log => _log.ToString();

        private sealed class TaggedWriter(StringBuilder log, string tag) : TextWriter
        {
            private readonly StringBuilder _pending = new();

            public override Encoding Encoding => Encoding.UTF8;

            public override void Write(char value)
            {
                if (value != '\n')
                {
                    _pending.Append(value);
                    return;
                }

                /* A newline emits even on an empty buffer: WriteLine() with no argument is a real,
                   order-carrying line, and swallowing it would hide a trailing STDERR write. */
                Emit();
            }

            /// <summary>Emits whatever is buffered without waiting for a newline, so a trailing
            /// <c>Write</c> with no line break still lands in the ordered log instead of vanishing.</summary>
            public override void Flush()
            {
                if (_pending.Length > 0)
                {
                    Emit();
                }
            }

            private void Emit()
            {
                log.Append(tag).Append(": ").Append(_pending.ToString().TrimEnd('\r')).Append('\n');
                _pending.Clear();
            }
        }
    }
}
