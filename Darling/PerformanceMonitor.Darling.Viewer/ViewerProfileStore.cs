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
using System.Text.Json;
using PerformanceMonitor.Common;

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// The viewer's registry of named, reusable <see cref="ViewerCredentialProfile"/> records — the Darling
/// port of Lite's <c>ProfileManager</c>, backing the Add / Edit / Delete surfaces launched from
/// <see cref="ManageServersWindow"/>. Persists a bare list as indented JSON in the viewer's per-user
/// directory (%APPDATA%\PerformanceMonitorDarling\viewer-profiles.json), alongside
/// <see cref="ViewerServerStore"/>'s registry, and stores each profile's secret in Windows Credential
/// Manager keyed by <c>profile_{id}</c> (never in the JSON) through the SAME
/// <see cref="IViewerServerSecretStore"/> the server store uses — the <c>profile_</c> prefix keeps
/// profile secrets from colliding with per-server secrets (bare GUID keys).
///
/// <para>Holds a ONE-WAY reference to <see cref="ViewerServerStore"/> for the referential-integrity
/// (in-use) query only: a profile can't be deleted while a server still references it. Both the on-disk
/// path and the secret store are injectable so tests round-trip against a temp file and an in-memory
/// secret fake without touching the real registry or vault. As with the rest of the Lite→Darling port,
/// this persists faithfully; the running SERVICE resolving a profile-backed connection is separate wiring
/// flagged in the PR (the viewer itself never opens SqlClient — only credential MANAGEMENT lives here).</para>
/// </summary>
public sealed class ViewerProfileStore
{
    /// <summary>Credential Manager key prefix for profile secrets, combined with the bare id → <c>profile_{id}</c>.</summary>
    public const string ProfileKeyPrefix = "profile_";

    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    private readonly string _filePath;
    private readonly IViewerServerSecretStore _secrets;
    private readonly ViewerServerStore _serverStore;
    private readonly List<ViewerCredentialProfile> _profiles;

    /// <summary>Builds the Credential Manager key for a profile's secret (<c>profile_{id}</c>).</summary>
    public static string ProfileCredentialId(string profileId) => $"{ProfileKeyPrefix}{profileId}";

    /// <param name="serverStore">The server registry, queried for the referential-integrity (in-use) check.</param>
    /// <param name="filePath">Override the on-disk location (tests pass a temp file); null uses <see cref="DefaultFilePath"/>.</param>
    /// <param name="secretStore">Override the credential vault (tests pass an in-memory fake); null uses the real Credential Manager.</param>
    public ViewerProfileStore(ViewerServerStore serverStore, string? filePath = null, IViewerServerSecretStore? secretStore = null)
    {
        _serverStore = serverStore ?? throw new ArgumentNullException(nameof(serverStore));
        _filePath = filePath ?? DefaultFilePath();
        _secrets = secretStore ?? new WindowsCredentialServerSecretStore();
        _profiles = LoadFromDisk();
    }

    /// <summary>The resolved registry file path (surfaced for tests and diagnostics).</summary>
    public string FilePath => _filePath;

    /// <summary>%APPDATA%\PerformanceMonitorDarling\viewer-profiles.json.</summary>
    public static string DefaultFilePath()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PerformanceMonitorDarling");
        return Path.Combine(directory, "viewer-profiles.json");
    }

    /// <summary>All profiles, sorted by display name (case-insensitive). Fresh snapshot; safe to mutate.</summary>
    public List<ViewerCredentialProfile> GetAll() =>
        _profiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The profile with this id, or null.</summary>
    public ViewerCredentialProfile? GetProfile(string id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        return _profiles.FirstOrDefault(p => p.Id == id);
    }

    /// <summary>
    /// Adds a new profile and stores its secret. For SqlServer/ServicePrincipal the secret goes to
    /// Credential Manager under <c>profile_{id}</c>; ManagedIdentity stores none. Rejects a duplicate id
    /// or (per-process) a duplicate display name.
    /// </summary>
    public void AddProfile(ViewerCredentialProfile profile, string? username = null, string? secret = null)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (_profiles.Any(p => p.Id == profile.Id))
        {
            throw new InvalidOperationException($"Profile with ID {profile.Id} already exists");
        }
        if (_profiles.Any(p => string.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"A profile named '{profile.Name}' already exists");
        }

        _profiles.Add(profile);
        StoreSecretIfNeeded(profile, username, secret);
        Save();
    }

    /// <summary>
    /// Updates an existing profile (and re-stores its secret when a fresh one is supplied). Editing a
    /// profile's secret intentionally affects ALL servers that reference it. A blank secret on edit leaves
    /// the stored secret unchanged; switching to ManagedIdentity clears any stale stored secret.
    /// </summary>
    public void UpdateProfile(ViewerCredentialProfile profile, string? username = null, string? secret = null)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var existing = _profiles.FirstOrDefault(p => p.Id == profile.Id)
            ?? throw new InvalidOperationException($"Profile with ID {profile.Id} not found");
        if (_profiles.Any(p => p.Id != profile.Id &&
                               string.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"A profile named '{profile.Name}' already exists");
        }

        _profiles[_profiles.IndexOf(existing)] = profile;

        if (profile.AuthType == AuthenticationTypes.ManagedIdentity)
        {
            /* MI never has a secret; clear any stale one (e.g. profile retyped from SP -> MI). */
            _secrets.Delete(ProfileCredentialId(profile.Id));
        }
        else if (!string.IsNullOrEmpty(username) && secret is not null)
        {
            /* Only re-store when a fresh secret was supplied (a blank PasswordBox on edit means "leave it"). */
            _secrets.Save(ProfileCredentialId(profile.Id), username, secret);
        }

        Save();
    }

    /// <summary>
    /// Deletes a profile and its stored secret. Blocked (throws) while ANY server references it —
    /// referential integrity is a delete-constraint, not a cascade.
    /// </summary>
    public void DeleteProfile(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        var inUse = CountServersUsingProfile(id);
        if (inUse > 0)
        {
            throw new InvalidOperationException(
                $"This profile is used by {inUse} server(s). Reassign or remove them first.");
        }

        var profile = _profiles.FirstOrDefault(p => p.Id == id);
        if (profile is not null)
        {
            _profiles.Remove(profile);
            _secrets.Delete(ProfileCredentialId(id));
            Save();
        }
    }

    /// <summary>The servers (in the registry) that reference the given profile id.</summary>
    public int CountServersUsingProfile(string profileId)
    {
        if (string.IsNullOrEmpty(profileId))
        {
            return 0;
        }

        return _serverStore.GetAllServers().Count(s => s.CredentialProfileId == profileId);
    }

    /// <summary>
    /// Whether a profile's secret is present in Credential Manager. SqlServer/ServicePrincipal require
    /// one; ManagedIdentity needs none (returns true).
    /// </summary>
    public bool HasStoredSecret(ViewerCredentialProfile profile)
    {
        if (profile.AuthType == AuthenticationTypes.ManagedIdentity)
        {
            return true;
        }

        return _secrets.Find(ProfileCredentialId(profile.Id)) is not null;
    }

    /// <summary>The stored (username, secret) for a profile, or null — used to pre-fill the profile editor on edit.</summary>
    public (string Username, string Password)? GetSecret(string profileId) =>
        _secrets.Find(ProfileCredentialId(profileId));

    private void StoreSecretIfNeeded(ViewerCredentialProfile profile, string? username, string? secret)
    {
        if (profile.AuthType == AuthenticationTypes.ManagedIdentity)
        {
            return; // no secret
        }

        if (!string.IsNullOrEmpty(username) && secret is not null)
        {
            _secrets.Save(ProfileCredentialId(profile.Id), username, secret);
        }
    }

    private List<ViewerCredentialProfile> LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return new List<ViewerCredentialProfile>();
            }

            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<List<ViewerCredentialProfile>>(json, s_jsonOptions)
                ?? new List<ViewerCredentialProfile>();
        }
        catch (Exception ex)
        {
            /* A corrupt or unreadable registry must never block startup — begin empty and log. */
            ViewerLogger.Warn("ViewerProfileStore", $"Failed to load '{_filePath}': {ex.Message}");
            return new List<ViewerCredentialProfile>();
        }
    }

    private void Save()
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(_filePath, JsonSerializer.Serialize(_profiles, s_jsonOptions));
    }
}
