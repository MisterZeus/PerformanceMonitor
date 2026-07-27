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
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading.Tasks;
using PerformanceMonitor.Darling.Service;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// The DPAPI-credential ACL hardening (V8 security hardening, #1262). Windows-gated (ACLs are a
/// Windows concept). Proves the created files/dirs carry a PROTECTED DACL (inheritance stripped) with
/// no world-readable ACE (Everyone / Authenticated Users / Users), SYSTEM + Administrators + the
/// service account granted, INTERACTIVE granted only where the operator's Viewer must read
/// (admin/viewer credentials), and the trusted-owner guard accepting a file this process created.
/// </summary>
public sealed class DarlingFileSecurityTests
{
    private static readonly SecurityIdentifier s_system = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier s_administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier s_interactive = new(WellKnownSidType.InteractiveSid, null);
    private static readonly SecurityIdentifier s_everyone = new(WellKnownSidType.WorldSid, null);
    private static readonly SecurityIdentifier s_authenticatedUsers = new(WellKnownSidType.AuthenticatedUserSid, null);
    private static readonly SecurityIdentifier s_builtinUsers = new(WellKnownSidType.BuiltinUsersSid, null);

    [Fact]
    public void HardenFile_SuperuserCredential_LocksOutWorldAndInteractive()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "ACLs are Windows-only.");

        var path = Path.Combine(Path.GetTempPath(), "darling-acl-owner-" + Guid.NewGuid().ToString("N") + ".dpapi");
        File.WriteAllText(path, "blob");
        try
        {
            DarlingFileSecurity.HardenFile(path, allowInteractiveRead: false);

            var rules = ReadRules(new FileInfo(path).GetAccessControl());
            AssertProtectedAndNoWorldRead(new FileInfo(path).GetAccessControl(), rules);

            /* SYSTEM + Administrators present; INTERACTIVE absent (the Viewer never reads the superuser cred). */
            Assert.Contains(rules, r => r.sid.Equals(s_system));
            Assert.Contains(rules, r => r.sid.Equals(s_administrators));
            Assert.DoesNotContain(rules, r => r.sid.Equals(s_interactive));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void HardenFile_RoleCredential_GrantsInteractiveRead()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "ACLs are Windows-only.");

        var path = Path.Combine(Path.GetTempPath(), "darling-acl-admin-" + Guid.NewGuid().ToString("N") + ".dpapi");
        File.WriteAllText(path, "blob");
        try
        {
            DarlingFileSecurity.HardenFile(path, allowInteractiveRead: true);

            var security = new FileInfo(path).GetAccessControl();
            var rules = ReadRules(security);
            AssertProtectedAndNoWorldRead(security, rules);

            /* The operator's Viewer (interactive) can READ, but INTERACTIVE gets no more than read. */
            var interactive = rules.Where(r => r.sid.Equals(s_interactive)).ToList();
            Assert.NotEmpty(interactive);
            Assert.All(interactive, r => Assert.True(
                (r.rights & FileSystemRights.Read) == FileSystemRights.Read
                && (r.rights & FileSystemRights.Write) == 0,
                $"INTERACTIVE should be read-only, was {r.rights}"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void HardenDirectory_StripsInheritance_AndGrantsInteractiveTraverseOnly()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "ACLs are Windows-only.");

        var path = Path.Combine(Path.GetTempPath(), "darling-acl-dir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            DarlingFileSecurity.HardenDirectory(path, allowInteractiveTraverse: true);

            var security = new DirectoryInfo(path).GetAccessControl();
            var rules = ReadRules(security);
            AssertProtectedAndNoWorldRead(security, rules);

            Assert.Contains(rules, r => r.sid.Equals(s_system));
            Assert.Contains(rules, r => r.sid.Equals(s_administrators));

            /* INTERACTIVE gets traverse (execute) but NOT list/read-data, and only on this folder. */
            var interactive = rules.Where(r => r.sid.Equals(s_interactive)).ToList();
            Assert.NotEmpty(interactive);
            Assert.All(interactive, r =>
            {
                Assert.True((r.rights & FileSystemRights.ExecuteFile) == FileSystemRights.ExecuteFile, "traverse missing");
                Assert.True((r.rights & FileSystemRights.ListDirectory) == 0, "should not grant list");
                Assert.Equal(InheritanceFlags.None, r.inheritance);
            });
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public void IsTrustedOwner_TrueForAFileThisProcessCreated()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "ACLs are Windows-only.");

        var path = Path.Combine(Path.GetTempPath(), "darling-acl-owner-check-" + Guid.NewGuid().ToString("N") + ".dpapi");
        File.WriteAllText(path, "blob");
        try
        {
            /* Created by this process => owned by the service account (this identity) => trusted. */
            Assert.True(DarlingFileSecurity.IsTrustedOwner(path));
        }
        finally
        {
            File.Delete(path);
        }

        /* A path that doesn't exist is not trusted (fails closed). */
        Assert.False(DarlingFileSecurity.IsTrustedOwner(path));
    }

    /* ---- #1647: the verification half — is a secret-bearing file still readable by ordinary users? ----
       darling.json carries every monitored server's encryptedPassword plus the MCP and web tokens, all under
       DPAPI LocalMachine scope with an entropy constant published in this repo, so READ access to the file IS
       the secret. It never got an ACL: it sits beside the binary, and the documented install (extract to
       C:\PerformanceMonitorDarling) inherits BUILTIN\Users: Read & Execute from the root DACL. The service now
       hardens it at startup and raises a Critical when it is still exposed — this is the check behind that. */

    [Fact]
    public void IsReadableByOrdinaryUsers_TrueWhenUsersHoldsAReadAce_FalseAfterHardening()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "ACLs are Windows-only.");

        var path = Path.Combine(Path.GetTempPath(), "darling-config-acl-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, "{}");
        try
        {
            /* Reproduce what an install under C:\ inherits: BUILTIN\Users allowed Read. Added explicitly
               because %TEMP% is per-user and grants Users nothing to inherit. */
            var exposed = new FileInfo(path).GetAccessControl();
            exposed.AddAccessRule(new FileSystemAccessRule(
                s_builtinUsers, FileSystemRights.Read, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(exposed);

            Assert.True(
                DarlingFileSecurity.IsReadableByOrdinaryUsers(path),
                "a file granting BUILTIN\\Users read must be reported as exposed");

            /* The fix the service applies. HardenFile protects the DACL and drops every inherited ACE, so the
               Users grant is gone even though INTERACTIVE read is added for the Viewer. */
            DarlingFileSecurity.HardenFile(path, allowInteractiveRead: true);

            Assert.False(
                DarlingFileSecurity.IsReadableByOrdinaryUsers(path),
                "after hardening, no Users/Authenticated Users/Everyone read ACE may survive");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(WellKnownSidType.AuthenticatedUserSid)]
    [InlineData(WellKnownSidType.WorldSid)]
    public void IsReadableByOrdinaryUsers_AlsoCatchesAuthenticatedUsersAndEveryone(WellKnownSidType sidType)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "ACLs are Windows-only.");

        var path = Path.Combine(Path.GetTempPath(), "darling-config-acl-grp-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, "{}");
        try
        {
            var security = new FileInfo(path).GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(sidType, null), FileSystemRights.Read, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);

            Assert.True(DarlingFileSecurity.IsReadableByOrdinaryUsers(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void IsReadableByOrdinaryUsers_IgnoresAMetadataOnlyGrant_AndADenyAce()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "ACLs are Windows-only.");

        var path = Path.Combine(Path.GetTempPath(), "darling-config-acl-meta-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, "{}");
        try
        {
            DarlingFileSecurity.HardenFile(path, allowInteractiveRead: true);

            var security = new FileInfo(path).GetAccessControl();
            /* ReadAttributes/ReadPermissions grant no BYTES. Testing against the composite
               FileSystemRights.Read mask would call this "readable" and cry wolf on a common,
               harmless ACE — hence the check keys on ReadData specifically. */
            security.AddAccessRule(new FileSystemAccessRule(
                s_builtinUsers,
                FileSystemRights.ReadAttributes | FileSystemRights.ReadPermissions,
                AccessControlType.Allow));
            /* A DENY ACE is not a grant either. */
            security.AddAccessRule(new FileSystemAccessRule(
                s_authenticatedUsers, FileSystemRights.Read, AccessControlType.Deny));
            new FileInfo(path).SetAccessControl(security);

            Assert.False(DarlingFileSecurity.IsReadableByOrdinaryUsers(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void IsReadableByOrdinaryUsers_FalseForAnUnreadableDacl_RatherThanThrowing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "ACLs are Windows-only.");

        /* A missing file has no DACL to read. It returns false rather than throwing or crying wolf: the
           caller has ALREADY logged loudly if its harden attempt failed, and a Critical raised on an
           unreadable DACL would be noise that trains operators to ignore the real one. */
        Assert.False(DarlingFileSecurity.IsReadableByOrdinaryUsers(
            Path.Combine(Path.GetTempPath(), "darling-config-absent-" + Guid.NewGuid().ToString("N") + ".json")));
    }

    [Fact]
    public void HardenDirectory_OnTheCredentialParent_LeavesTheSiblingLogDirectoryWritable()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "ACLs are Windows-only.");

        /* Mirrors the REAL on-disk layout. DarlingManagedPostgres hardens ParentOf(dataDirectory) —
           %ProgramData%\PerformanceMonitorDarling — which ALSO contains the service's own `logs`
           directory (a sibling of the `pg` data dir). #1581 asked whether that credential lockdown is
           what silenced file logging in the field. It is NOT: HardenDirectory grants
           WindowsIdentity.GetCurrent().User — the identity the service is actually running as — Full
           Control with ContainerInherit|ObjectInherit, so `logs` INHERITS write access and the running
           service can never lock itself out. Proven with real I/O, not just ACE inspection, because
           that is the claim that matters. A field ACL failure therefore means something EXTERNAL
           re-ACL'd the path (on the field box, a SYSTEM-context SSM operation on one log file), not
           that the hardening scope is wrong — so DO NOT "fix" this by narrowing the scope off the
           parent, which would drop the inherited protection from the data dir subtree (server.key). */
        var parent = Path.Combine(Path.GetTempPath(), "darling-acl-parent-" + Guid.NewGuid().ToString("N"));
        var logs = Path.Combine(parent, "logs");
        Directory.CreateDirectory(logs);
        var existingLog = Path.Combine(logs, "darling-service_existing.log");
        File.WriteAllText(existingLog, "before\n");
        try
        {
            /* Exactly what DarlingManagedPostgres does before initdb. */
            DarlingFileSecurity.HardenDirectory(parent, allowInteractiveTraverse: true);

            /* 1. An already-open-style log file stays APPENDABLE (the flush path). */
            File.AppendAllText(existingLog, "after\n");
            Assert.Equal("before\nafter\n", File.ReadAllText(existingLog));

            /* 2. A NEW log file — the daily-rotation case — can still be created and written. */
            var rotatedLog = Path.Combine(logs, "darling-service_rotated.log");
            File.WriteAllText(rotatedLog, "rotated\n");
            Assert.Equal("rotated\n", File.ReadAllText(rotatedLog));

            /* 3. A subdirectory created AFTER hardening — the initdb data-dir case — is writable, which
                  is the whole reason the parent is hardened first (the subtree inherits). */
            var dataDir = Path.Combine(parent, "pg");
            Directory.CreateDirectory(dataDir);
            var serverKey = Path.Combine(dataDir, "server.key");
            File.WriteAllText(serverKey, "key\n");
            Assert.Equal("key\n", File.ReadAllText(serverKey));

            /* 4. The lockdown DID reach both (no world-readable principal survives) — the log directory
                  really is swept into the credential ACL; it is simply still writable by the service. */
            foreach (var swept in new[] { logs, dataDir })
            {
                var rules = ReadRules(new DirectoryInfo(swept).GetAccessControl());
                Assert.DoesNotContain(rules, r => r.sid.Equals(s_everyone));
                Assert.DoesNotContain(rules, r => r.sid.Equals(s_authenticatedUsers));
                Assert.DoesNotContain(rules, r => r.sid.Equals(s_builtinUsers));
            }
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    private static (SecurityIdentifier sid, FileSystemRights rights, InheritanceFlags inheritance)[] ReadRules(
        FileSystemSecurity security)
    {
        return security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(r => r.AccessControlType == AccessControlType.Allow)
            .Select(r => ((SecurityIdentifier)r.IdentityReference, r.FileSystemRights, r.InheritanceFlags))
            .ToArray();
    }

    [Fact]
    public async Task WriteWithBackup_HardensTheBackup_BecauseFileCopyDoesNotCarryTheDacl()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "ACLs are Windows-only.");

        /* The install location this reproduces is a folder created directly under C:\, whose root DACL
           grants BUILTIN\Users an INHERITABLE read. %TEMP% is per-user and grants Users nothing, so the
           ACE is planted here deliberately — without it the backup would be clean for the wrong reason and
           this test would pass with the hardening removed. */
        var directory = Path.Combine(Path.GetTempPath(), "darling-cfg-bak-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(directory);
        try
        {
            var directorySecurity = new DirectoryInfo(directory).GetAccessControl();
            directorySecurity.AddAccessRule(new FileSystemAccessRule(
                s_builtinUsers,
                FileSystemRights.Read,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            new DirectoryInfo(directory).SetAccessControl(directorySecurity);

            var configPath = Path.Combine(directory, "darling.json");
            await File.WriteAllTextAsync(configPath, "{\"servers\":[]}");

            /* Proves the planted ACE actually flows to a new file in this directory — i.e. that the
               scenario under test is real on this machine, not just asserted. */
            var canary = Path.Combine(directory, "canary.json");
            await File.WriteAllTextAsync(canary, "{}");
            Assert.True(
                DarlingFileSecurity.IsReadableByOrdinaryUsers(canary),
                "a new file in this directory must inherit the Users read ACE, or the test proves nothing");

            var written = await DarlingCliCommands.WriteWithBackupAsync(
                configPath, "{\"servers\":[],\"edited\":true}", TextWriter.Null, TextWriter.Null, default);
            Assert.True(written);

            var backup = Directory.GetFiles(directory, "darling.json.bak-*").Single();
            Assert.False(
                DarlingFileSecurity.IsReadableByOrdinaryUsers(backup),
                "the backup is a full copy of the encrypted passwords and access tokens — File.Copy does not " +
                "carry the source DACL, so an unhardened backup hands every local user the credentials that " +
                "darling.json's own ACL exists to protect");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void AssertProtectedAndNoWorldRead(
        FileSystemSecurity security, (SecurityIdentifier sid, FileSystemRights rights, InheritanceFlags inheritance)[] rules)
    {
        /* Inheritance stripped: the ACL is exactly what we set, nothing inherited from %ProgramData%. */
        Assert.True(security.AreAccessRulesProtected, "DACL must be protected (inheritance disabled)");

        /* No world-readable principal survives. */
        Assert.DoesNotContain(rules, r => r.sid.Equals(s_everyone));
        Assert.DoesNotContain(rules, r => r.sid.Equals(s_authenticatedUsers));
        Assert.DoesNotContain(rules, r => r.sid.Equals(s_builtinUsers));
    }
}
