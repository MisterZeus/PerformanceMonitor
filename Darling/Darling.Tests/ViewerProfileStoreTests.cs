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
using PerformanceMonitor.Common;
using PerformanceMonitor.Darling.Viewer;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// Pins the viewer's credential-profile registry (<see cref="ViewerProfileStore"/>, the port of Lite's
/// ProfileManager): JSON round-trip, secret keying under <c>profile_{id}</c> (never the JSON, never the
/// bare server key), ManagedIdentity's no-secret rule, CRUD, per-process name uniqueness, and the
/// referential-integrity delete-constraint (blocked while a server references it, allowed after reassign).
/// Every test uses a temp file and an in-memory secret fake, so nothing touches the real registry or
/// Windows Credential Manager.
/// </summary>
public sealed class ViewerProfileStoreTests : IDisposable
{
    private readonly string _profilePath = Path.Combine(Path.GetTempPath(), $"viewer-profiles-{Guid.NewGuid():N}.json");
    private readonly string _serverPath = Path.Combine(Path.GetTempPath(), $"viewer-servers-{Guid.NewGuid():N}.json");
    private readonly FakeSecretStore _profileSecrets = new();
    private readonly FakeSecretStore _serverSecrets = new();

    private ViewerServerStore NewServerStore() => new(_serverPath, _serverSecrets);
    private ViewerProfileStore NewProfileStore(ViewerServerStore serverStore) => new(serverStore, _profilePath, _profileSecrets);

    public void Dispose()
    {
        foreach (var path in new[] { _profilePath, _serverPath })
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* best-effort temp cleanup */ }
        }
    }

    [Fact]
    public void AddProfile_RoundTripsThroughDisk_AndStoresSecretUnderProfileKey_NotJson()
    {
        var store = NewProfileStore(NewServerStore());
        var profile = new ViewerCredentialProfile
        {
            Name = "sql-fleet",
            AuthType = AuthenticationTypes.SqlServer,
            Username = "sa"
        };

        store.AddProfile(profile, "sa", "the-password");

        /* Secret under profile_{id}, NOT a bare id key. */
        Assert.NotNull(_profileSecrets.Find(ViewerProfileStore.ProfileCredentialId(profile.Id)));
        Assert.Null(_profileSecrets.Find(profile.Id));

        /* viewer-profiles.json holds the profile, never the secret. */
        var json = File.ReadAllText(_profilePath);
        Assert.Contains("sql-fleet", json);
        Assert.DoesNotContain("the-password", json);

        /* A fresh store over the same file reloads it. */
        var reloaded = NewProfileStore(NewServerStore()).GetProfile(profile.Id);
        Assert.NotNull(reloaded);
        Assert.Equal("sql-fleet", reloaded!.Name);
        Assert.Equal(AuthenticationTypes.SqlServer, reloaded.AuthType);
        Assert.Equal("sa", reloaded.Username);
    }

    [Fact]
    public void ManagedIdentityProfile_StoresNoSecret_ButHasStoredSecretIsTrue()
    {
        var store = NewProfileStore(NewServerStore());
        var profile = new ViewerCredentialProfile { Name = "mi", AuthType = AuthenticationTypes.ManagedIdentity };

        store.AddProfile(profile, username: null, secret: null);

        Assert.Null(_profileSecrets.Find(ViewerProfileStore.ProfileCredentialId(profile.Id)));
        Assert.True(store.HasStoredSecret(profile));  // MI needs none
    }

    [Fact]
    public void GetAll_SortsByName()
    {
        var store = NewProfileStore(NewServerStore());
        store.AddProfile(new ViewerCredentialProfile { Name = "Zeta", AuthType = AuthenticationTypes.ManagedIdentity });
        store.AddProfile(new ViewerCredentialProfile { Name = "alpha", AuthType = AuthenticationTypes.ManagedIdentity });

        Assert.Equal(new[] { "alpha", "Zeta" }, store.GetAll().Select(p => p.Name));
    }

    [Fact]
    public void AddProfile_RejectsDuplicateName()
    {
        var store = NewProfileStore(NewServerStore());
        store.AddProfile(new ViewerCredentialProfile { Name = "dup", AuthType = AuthenticationTypes.ManagedIdentity });

        Assert.Throws<InvalidOperationException>(() =>
            store.AddProfile(new ViewerCredentialProfile { Name = "dup", AuthType = AuthenticationTypes.ManagedIdentity }));
    }

    [Fact]
    public void UpdateProfile_BlankSecret_LeavesStoredSecretUnchanged_FreshSecretReplacesIt()
    {
        var store = NewProfileStore(NewServerStore());
        var profile = new ViewerCredentialProfile { Name = "p", AuthType = AuthenticationTypes.SqlServer, Username = "sa" };
        store.AddProfile(profile, "sa", "pw1");

        /* Rename only (blank secret) → the stored secret is untouched. */
        profile.Name = "renamed";
        store.UpdateProfile(profile);
        Assert.Equal("pw1", _profileSecrets.Find(ViewerProfileStore.ProfileCredentialId(profile.Id))!.Value.Password);

        /* Fresh secret → replaces it. */
        store.UpdateProfile(profile, "sa", "pw2");
        Assert.Equal("pw2", _profileSecrets.Find(ViewerProfileStore.ProfileCredentialId(profile.Id))!.Value.Password);
        Assert.Equal("renamed", NewProfileStore(NewServerStore()).GetProfile(profile.Id)!.Name);
    }

    [Fact]
    public void DeleteProfile_RemovesProfileAndSecret()
    {
        var store = NewProfileStore(NewServerStore());
        var profile = new ViewerCredentialProfile { Name = "to-delete", AuthType = AuthenticationTypes.ServicePrincipal, AzureClientId = "cid" };
        store.AddProfile(profile, "cid", "sec");

        store.DeleteProfile(profile.Id);

        Assert.Null(store.GetProfile(profile.Id));
        Assert.Null(_profileSecrets.Find(ViewerProfileStore.ProfileCredentialId(profile.Id)));
        Assert.Empty(NewProfileStore(NewServerStore()).GetAll());
    }

    [Fact]
    public void DeleteProfile_BlockedWhileServerReferencesIt_AllowedAfterReassign()
    {
        var serverStore = NewServerStore();
        var profileStore = NewProfileStore(serverStore);

        var profile = new ViewerCredentialProfile { Name = "shared", AuthType = AuthenticationTypes.SqlServer, Username = "sa" };
        profileStore.AddProfile(profile, "sa", "pw");

        var server = new ViewerServerEntry { ServerName = "s", DisplayName = "s", CredentialProfileId = profile.Id };
        serverStore.AddServer(server, null, null);

        Assert.Equal(1, profileStore.CountServersUsingProfile(profile.Id));

        /* Blocked while referenced. */
        var ex = Assert.Throws<InvalidOperationException>(() => profileStore.DeleteProfile(profile.Id));
        Assert.Contains("used by", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(profileStore.GetProfile(profile.Id));

        /* Reassign (clear the reference) then delete succeeds. */
        server.CredentialProfileId = null;
        serverStore.UpdateServer(server);
        profileStore.DeleteProfile(profile.Id);
        Assert.Null(profileStore.GetProfile(profile.Id));
    }

    /// <summary>In-memory <see cref="IViewerServerSecretStore"/> so tests never touch Windows Credential Manager.</summary>
    private sealed class FakeSecretStore : IViewerServerSecretStore
    {
        private readonly Dictionary<string, (string Username, string Password)> _map = new();

        public void Save(string id, string username, string password) => _map[id] = (username, password);

        public (string Username, string Password)? Find(string id) =>
            _map.TryGetValue(id, out var v) ? v : null;

        public void Delete(string id) => _map.Remove(id);
    }
}
