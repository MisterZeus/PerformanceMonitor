/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PerformanceMonitorLite.Services;
using Xunit;

namespace PerformanceMonitorLite.Tests;

/// <summary>
/// #2433. Lite's Settings window said "Settings saved." on a save that wrote nothing.
///
/// <para>Ten writers backed that one button and each rewrote the whole of settings.json behind its own
/// catch. Seven returned <c>void</c> and could not report a write failure by construction; the three that
/// returned something returned whether the BOXES validated, which is a different question. So the toast
/// was shown whenever nothing objected, and "nothing objected" was never about whether a byte reached
/// disk.</para>
///
/// <para>The repair was to remove the question rather than answer it: one read, ten mutators on one
/// document, one write. That leaves a single ordering rule, which is what these pin — and the rule that
/// matters is that a failed write outranks a validation objection. A writer that rejected a value has
/// already raised its own dialog naming it; what the user has not been told, and can find out nowhere
/// else, is that none of it was saved.</para>
/// </summary>
public sealed class SettingsSaveReportTests
{
    [Fact]
    public void AnOrdinarySave_Reports_Saved()
    {
        Assert.Equal(
            SettingsSaveOutcome.Saved,
            SettingsSaveReport.Classify(documentWritten: true, mcpChanged: false,
                alertsValid: true, mcpValid: true, webhooksValid: true));
    }

    [Fact]
    public void AnMcpChange_AsksForARestart()
    {
        Assert.Equal(
            SettingsSaveOutcome.SavedAndMcpNeedsRestart,
            SettingsSaveReport.Classify(documentWritten: true, mcpChanged: true,
                alertsValid: true, mcpValid: true, webhooksValid: true));
    }

    /// <summary>
    /// The headline case. Everything on the page validated, so on dev this is exactly the state that
    /// produced "Settings saved." over a save that wrote nothing at all.
    /// </summary>
    [Fact]
    public void AFailedWrite_IsNeverReportedAsSaved()
    {
        Assert.Equal(
            SettingsSaveOutcome.NothingWritten,
            SettingsSaveReport.Classify(documentWritten: false, mcpChanged: false,
                alertsValid: true, mcpValid: true, webhooksValid: true));
    }

    /// <summary>
    /// And it stays the headline case when a writer ALSO objected. The objection has its own dialog; the
    /// failed write does not, and it is the one that describes what happened to the other nine writers'
    /// work. Ordering the other way round would leave the most important fact of the save unsaid.
    /// </summary>
    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void AFailedWrite_OutranksAValidationObjection(bool alertsValid, bool mcpValid, bool webhooksValid)
    {
        Assert.Equal(
            SettingsSaveOutcome.NothingWritten,
            SettingsSaveReport.Classify(documentWritten: false, mcpChanged: true,
                alertsValid, mcpValid, webhooksValid));
    }

    /// <summary>
    /// A rejected value suppresses the toast without claiming nothing was saved — because the rest of the
    /// document DID reach disk. This is the arm that would be a lie under any of the three options #2433
    /// listed that kept the writes separate.
    /// </summary>
    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void AnObjection_SuppressesTheToast_WithoutClaimingNothingWasSaved(
        bool alertsValid, bool mcpValid, bool webhooksValid)
    {
        Assert.Equal(
            SettingsSaveOutcome.WrittenWithObjections,
            SettingsSaveReport.Classify(documentWritten: true, mcpChanged: false,
                alertsValid, mcpValid, webhooksValid));
    }
}

/// <summary>
/// The category behind #2433 rather than the instance, and the companion to
/// <see cref="SettingsWriterQuarantineWiringTests"/>'s one-writer pin: the Save button must ask whether the
/// write happened before it is allowed to say anything, and none of its writers may go behind its back to
/// settings.json.
///
/// <para>Source-parsing because the defect was a wiring one. Every individual writer was correct about the
/// thing it knew; what was missing was any path from "the write failed" to the sentence the user reads.</para>
/// </summary>
public sealed class SettingsSaveButtonHonestyTests
{
    private static string SettingsWindowSource() =>
        File.ReadAllText(FindRepoFile(Path.Combine("Lite", "Windows", "SettingsWindow.xaml.cs")));

    /// <summary>
    /// The toast is reachable only through the classifier, so a future edit cannot restore the old
    /// "nothing objected, therefore say it saved" shortcut without deleting this.
    /// </summary>
    [Fact]
    public void TheSaveButton_AsksTheClassifierBeforeItSaysAnything()
    {
        var body = MethodBody(SettingsWindowSource(), "SaveButton_Click");

        Assert.Contains("App.WriteSettingsDocument(", body, StringComparison.Ordinal);
        Assert.Contains("SettingsSaveReport.Classify(", body, StringComparison.Ordinal);

        /* Every "Settings saved" string in the method must sit under a classifier arm; the only way to
           check that cheaply is that the classify call comes first. */
        var classify = body.IndexOf("SettingsSaveReport.Classify(", StringComparison.Ordinal);
        var firstToast = body.IndexOf("Settings saved", StringComparison.Ordinal);
        Assert.True(classify < firstToast,
            "SaveButton_Click can reach \"Settings saved.\" without going through SettingsSaveReport.Classify, " +
            "which is #2433: the toast is about whether the boxes validated rather than whether anything " +
            "reached disk.");
    }

    /// <summary>
    /// Each Save* the button calls takes the shared document. A writer that opened settings.json for itself
    /// would be back to its own read, its own write and its own swallowed failure — one write is what makes
    /// one answer possible.
    /// </summary>
    [Fact]
    public void EveryWriterTheSaveButtonCalls_TakesTheSharedDocument()
    {
        var source = SettingsWindowSource();
        var body = MethodBody(source, "SaveButton_Click");

        var called = Regex.Matches(body, @"\b(Save[A-Za-z]+)\(root\)")
            .Select(m => m.Groups[1].Value)
            .ToList();

        Assert.True(called.Count >= 9,
            $"SaveButton_Click hands the shared document to only {called.Count} writer(s) — the rest are " +
            "opening settings.json for themselves again.");

        foreach (var name in called)
        {
            Assert.Matches(new Regex($@"\b{name}\(JsonNode root\)"), source);
        }
    }

    private static string MethodBody(string source, string methodName)
    {
        var signature = source.IndexOf(methodName + "(", StringComparison.Ordinal);
        Assert.True(signature >= 0, $"No method named {methodName} — this guard's anchor moved and it is testing nothing.");

        var open = source.IndexOf('{', signature);
        Assert.True(open >= 0, $"{methodName} has no body.");

        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..i];
        }

        throw new InvalidOperationException($"{methodName}'s body is unbalanced.");
    }

    private static string FindRepoFile(string relativePath)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = Path.GetDirectoryName(dir);
        }

        throw new FileNotFoundException($"Could not locate {relativePath} walking up from {AppContext.BaseDirectory}");
    }
}
