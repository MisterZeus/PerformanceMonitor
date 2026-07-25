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
/// Pins the viewer's server-definition registry (<see cref="ViewerServerStore"/>) — JSON round-trip,
/// Lite's favorites-first ordering, ToggleFavorite (including the adopt-on-first-favorite path), CRUD,
/// and Import. Every test uses a temp file and an in-memory secret fake, so nothing touches the real
/// per-user profile or Windows Credential Manager.
/// </summary>
public sealed class ViewerServerStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"viewer-servers-{Guid.NewGuid():N}.json");
    private readonly FakeSecretStore _secrets = new();

    private ViewerServerStore NewStore() => new(_path, _secrets);

    public void Dispose()
    {
        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch
        {
            /* best-effort temp cleanup */
        }
    }

    [Fact]
    public void AddServer_RoundTripsThroughDisk()
    {
        var store = NewStore();
        store.AddServer(new ViewerServerEntry
        {
            ServerName = "SQL2022",
            DisplayName = "Prod",
            AuthenticationType = AuthenticationTypes.SqlServer,
            MonthlyCostUsd = 250m,
            ReadOnlyIntent = true,
            ExcludedDatabases = new List<string> { "junk" }
        }, "sa", "secret");

        /* A fresh store over the same file must see the persisted definition. */
        var reloaded = NewStore().GetAllServers();
        var entry = Assert.Single(reloaded);
        Assert.Equal("SQL2022", entry.ServerName);
        Assert.Equal("Prod", entry.DisplayName);
        Assert.Equal(AuthenticationTypes.SqlServer, entry.AuthenticationType);
        Assert.Equal(250m, entry.MonthlyCostUsd);
        Assert.True(entry.ReadOnlyIntent);
        Assert.Equal("Prod (Read-Only)", entry.DisplayNameWithIntent);
        Assert.Contains("junk", entry.ExcludedDatabases);
    }

    [Fact]
    public void AddServer_StoresSecret_AndGetCredentialReturnsIt()
    {
        var store = NewStore();
        var entry = new ViewerServerEntry { ServerName = "A", DisplayName = "A", AuthenticationType = AuthenticationTypes.SqlServer };
        store.AddServer(entry, "sa", "pw");

        var cred = store.GetCredential(entry.Id);
        Assert.True(cred.HasValue);
        Assert.Equal("sa", cred!.Value.Username);
        Assert.Equal("pw", cred.Value.Password);
    }

    [Fact]
    public void AddServer_WithoutCredentials_ClearsAnyStaleSecret()
    {
        var store = NewStore();
        var entry = new ViewerServerEntry { ServerName = "A", DisplayName = "A" };
        _secrets.Save(entry.Id, "old", "old");

        store.AddServer(entry, null, null);

        Assert.Null(store.GetCredential(entry.Id));
    }

    [Fact]
    public void GetAllServers_SortsFavoritesFirst_ThenByDisplayName()
    {
        var store = NewStore();
        store.AddServer(new ViewerServerEntry { ServerName = "z", DisplayName = "Zeta" }, null, null);
        store.AddServer(new ViewerServerEntry { ServerName = "a", DisplayName = "Alpha" }, null, null);
        store.AddServer(new ViewerServerEntry { ServerName = "m", DisplayName = "Mu", IsFavorite = true }, null, null);

        var order = store.GetAllServers().Select(s => s.DisplayName).ToList();

        Assert.Equal(new[] { "Mu", "Alpha", "Zeta" }, order);
    }

    [Fact]
    public void ToggleFavorite_AdoptsUnknownServer_ThenFlips()
    {
        var store = NewStore();

        Assert.True(store.ToggleFavorite("SQL2019"));      // adopt + pin
        Assert.True(store.IsFavorite("SQL2019"));
        Assert.Single(store.GetAllServers());

        Assert.False(store.ToggleFavorite("SQL2019"));     // unpin the same entry (not a second one)
        Assert.False(store.IsFavorite("SQL2019"));
        Assert.Single(store.GetAllServers());
    }

    [Fact]
    public void ToggleFavorite_IsCaseInsensitiveOnServerName()
    {
        var store = NewStore();
        store.AddServer(new ViewerServerEntry { ServerName = "SQL2022", DisplayName = "Prod" }, null, null);

        Assert.True(store.ToggleFavorite("sql2022"));

        Assert.True(store.IsFavorite("SQL2022"));
        Assert.Single(store.GetAllServers());   // flipped the existing entry, didn't add a duplicate
    }

    [Fact]
    public void UpdateServer_ReplacesById_AndPersists()
    {
        var store = NewStore();
        var entry = new ViewerServerEntry { ServerName = "A", DisplayName = "A" };
        store.AddServer(entry, null, null);

        entry.DisplayName = "Renamed";
        entry.MonthlyCostUsd = 99m;
        store.UpdateServer(entry);

        var reloaded = Assert.Single(NewStore().GetAllServers());
        Assert.Equal("Renamed", reloaded.DisplayName);
        Assert.Equal(99m, reloaded.MonthlyCostUsd);
    }

    [Fact]
    public void DeleteServer_RemovesEntry_AndDeletesSecret()
    {
        var store = NewStore();
        var entry = new ViewerServerEntry { ServerName = "A", DisplayName = "A", AuthenticationType = AuthenticationTypes.SqlServer };
        store.AddServer(entry, "sa", "pw");

        store.DeleteServer(entry.Id);

        Assert.Empty(NewStore().GetAllServers());
        Assert.Null(_secrets.Find(entry.Id));
    }

    [Fact]
    public void ImportServersFromFile_UpsertsByName_SkippingDuplicates()
    {
        /* Source registry written by another install. */
        var source = Path.Combine(Path.GetTempPath(), $"viewer-servers-src-{Guid.NewGuid():N}.json");
        var sourceStore = new ViewerServerStore(source, new FakeSecretStore());
        sourceStore.AddServer(new ViewerServerEntry { ServerName = "shared", DisplayName = "Shared" }, null, null);
        sourceStore.AddServer(new ViewerServerEntry { ServerName = "extra", DisplayName = "Extra" }, null, null);

        try
        {
            var store = NewStore();
            store.AddServer(new ViewerServerEntry { ServerName = "shared", DisplayName = "Existing" }, null, null);

            var (imported, skipped) = store.ImportServersFromFile(source);

            Assert.Equal(1, imported);   // "extra"
            Assert.Equal(1, skipped);    // "shared" already present
            Assert.Equal(2, store.GetAllServers().Count);
            Assert.NotNull(store.GetByServerName("extra"));
        }
        finally
        {
            try { File.Delete(source); } catch { /* best-effort */ }
        }
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
