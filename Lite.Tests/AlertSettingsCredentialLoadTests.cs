/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.IO;
using PerformanceMonitorLite;
using Xunit;

namespace PerformanceMonitorLite.Tests;

/// <summary>
/// #1832 second defect: webhook secrets live in Windows Credential Manager, never in settings.json, but
/// <see cref="App.LoadAlertSettings()"/> used to read them at the TAIL of its settings.json parse - behind
/// the "no settings.json, nothing to do" early exit. So on an install whose settings.json had been
/// destroyed (which is exactly what re-running Setup.exe over the old data root produced), the Settings
/// window's webhook boxes rendered EMPTY while the credentials were still in the store, and Save wrote the
/// blanks back through SaveWebhookUrl - which DELETES on blank. Looking at the settings window finished off
/// the secrets that had survived the data loss.
///
/// The reads must therefore happen regardless of settings.json. That is a wiring property: both halves
/// work fine in isolation and the bug lives entirely in the order they run in, so only a test that removes
/// settings.json and watches which credential keys get requested can see it.
///
/// Shares a collection with <see cref="AppAlertSettingsTests"/> because both write the App webhook statics
/// and xUnit runs separate classes in parallel. #1965 added the two alert-statics classes to the same
/// collection (renaming it "app-alert-statics"): App.LoadAlertSettings rewrites the whole alert block, not
/// just the webhook keys, so this class was a silent third party to that race.
/// </summary>
[Collection("app-alert-statics")]
public class AlertSettingsCredentialLoadTests
{
    private static readonly string[] ExpectedKeys =
        { "TeamsWebhook", "SlackWebhook", "GenericWebhook", "GenericWebhookHeaders" };

    private static string NewTempDir(string tag)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"pmlite_{tag}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// The credential WRITER for every case that must not write. The real one edits Windows Credential
    /// Manager for the user running the suite, so a case that unexpectedly reaches the #1506 legacy
    /// migration would overwrite a live webhook URL rather than fail a test.
    /// </summary>
    private static void FailOnWrite(string key, string value) =>
        Assert.Fail($"LoadAlertSettings wrote credential '{key}' when nothing should have been saved.");

    /// <summary>
    /// The regression itself: no settings.json, and all four secrets are still read and still land on the
    /// statics the Settings window binds its boxes to.
    /// </summary>
    [Fact]
    public void LoadAlertSettings_ReadsWebhookCredentials_WhenSettingsJsonIsMissing()
    {
        var configDir = NewTempDir("nosettings");
        try
        {
            Assert.False(File.Exists(Path.Combine(configDir, "settings.json")));

            var requested = new List<string>();
            App.LoadAlertSettings(configDir, key => { requested.Add(key); return "stored:" + key; }, FailOnWrite);

            foreach (var key in ExpectedKeys)
            {
                Assert.Contains(key, requested);
            }

            /* SettingsWindow populates its boxes straight off these, so a blank here IS the blank box that
               Save then wrote back as a delete. */
            Assert.Equal("stored:TeamsWebhook", App.TeamsWebhookUrl);
            Assert.Equal("stored:SlackWebhook", App.SlackWebhookUrl);
            Assert.Equal("stored:GenericWebhook", App.GenericWebhookUrl);
            Assert.Equal("stored:GenericWebhookHeaders", App.GenericWebhookHeadersJson);
        }
        finally
        {
            Directory.Delete(configDir, true);
        }
    }

    /// <summary>
    /// A settings.json that exists but is corrupt takes the catch, which is the same trap one layer in.
    /// </summary>
    [Fact]
    public void LoadAlertSettings_ReadsWebhookCredentials_WhenSettingsJsonIsCorrupt()
    {
        var configDir = NewTempDir("corrupt");
        try
        {
            File.WriteAllText(Path.Combine(configDir, "settings.json"), "{ this is not json");

            var requested = new List<string>();
            App.LoadAlertSettings(configDir, key => { requested.Add(key); return "stored:" + key; }, FailOnWrite);

            foreach (var key in ExpectedKeys)
            {
                Assert.Contains(key, requested);
            }

            Assert.Equal("stored:TeamsWebhook", App.TeamsWebhookUrl);
        }
        finally
        {
            Directory.Delete(configDir, true);
        }
    }

    /// <summary>
    /// The path that always worked keeps working: a real settings.json still gets parsed, and the secrets
    /// still come from the credential store rather than the file.
    /// </summary>
    [Fact]
    public void LoadAlertSettings_StillParsesSettings_AndStillPrefersTheCredentialStore()
    {
        var configDir = NewTempDir("present");
        try
        {
            File.WriteAllText(
                Path.Combine(configDir, "settings.json"),
                "{\"teams_webhook_enabled\":true,\"teams_proxy_address\":\"proxy.sentinel.test\"}");

            App.TeamsWebhookEnabled = false;
            App.LoadAlertSettings(configDir, key => "stored:" + key, FailOnWrite);

            Assert.True(App.TeamsWebhookEnabled);
            Assert.Equal("proxy.sentinel.test", App.TeamsProxyAddress);
            Assert.Equal("stored:TeamsWebhook", App.TeamsWebhookUrl);
        }
        finally
        {
            Directory.Delete(configDir, true);
        }
    }

    /// <summary>
    /// The #1506 legacy migration is unchanged by the hoist: a plaintext URL still in settings.json is
    /// written to the credential store AND becomes the live value, which is what the old tail read-back
    /// produced. The injected reader returns a different value, so a regression to "the stored value wins"
    /// is visible rather than coincidentally equal.
    /// </summary>
    [Fact]
    public void LoadAlertSettings_LegacyPlaintextUrl_StillOverridesTheStoredValue()
    {
        var configDir = NewTempDir("legacy");
        try
        {
            File.WriteAllText(
                Path.Combine(configDir, "settings.json"),
                "{\"teams_webhook_url\":\"https://legacy.sentinel.test/hook\"}");

            var written = new List<(string Key, string Value)>();
            App.LoadAlertSettings(configDir, key => "stored:" + key, (key, value) => written.Add((key, value)));

            Assert.Equal(("TeamsWebhook", "https://legacy.sentinel.test/hook"), Assert.Single(written));
            Assert.Equal("https://legacy.sentinel.test/hook", App.TeamsWebhookUrl);
        }
        finally
        {
            Directory.Delete(configDir, true);
        }
    }
}
