/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PerformanceMonitorLite.Services;

/// <summary>
/// What a read of settings.json found. Telling <see cref="Absent"/> from <see cref="Unreadable"/> is the
/// whole reason this type exists (#2425): the loaders collapsed both into one bare <c>catch</c>, so a
/// first run with no file and a hand-edit with a trailing comma took the identical silent path to
/// defaults. Absent is a legitimate first run, defaults are the right answer, and silence is correct.
/// Unreadable is never correct and is always worth saying out loud.
/// </summary>
public enum SettingsFileState
{
    /// <summary>No settings.json. First run, or the file was deliberately removed.</summary>
    Absent,

    /// <summary>Present and parsed to a JSON object.</summary>
    Readable,

    /// <summary>Present, but not usable as a JSON object: malformed, empty, unreadable from disk, or a
    /// root that is an array/string/number/null rather than an object.</summary>
    Unreadable
}

/// <summary>
/// The outcome of <see cref="SettingsFileGuard.Read"/>. <c>Root</c> and <c>Text</c> are non-null only for
/// <see cref="SettingsFileState.Readable"/>; <c>Problem</c> is non-null only for
/// <see cref="SettingsFileState.Unreadable"/>. <c>Text</c> is carried so a caller that wants the
/// <c>JsonDocument</c> reader rather than the <c>JsonNode</c> one does not have to read the file twice.
/// </summary>
public readonly record struct SettingsFileRead(
    SettingsFileState State,
    JsonObject? Root,
    string? Text,
    string? Problem);

/// <summary>
/// The outcome of <see cref="SettingsFileGuard.RootForWrite"/>. <c>Problem</c> is null on the ordinary
/// paths (the file parsed, or there was no file). When it is set, <c>QuarantinedTo</c> says where the
/// unreadable original was copied — and a null there means the copy could not be made, which the caller
/// must read as "do not write", because the alternative is destroying a file nobody ever understood.
/// </summary>
public readonly record struct SettingsWriteRoot(
    JsonObject Root,
    string? Problem,
    string? QuarantinedTo);

/// <summary>
/// The one place that decides whether settings.json can be read, and what to do about it when it cannot
/// (#2425).
///
/// <para>Kept free of WPF, of the logger and of any process-wide state on purpose. Every decision that
/// matters here — absent versus unreadable, what the user is told about a parse error, whether a Save is a
/// merge or a replacement, whether the old file is preserved first — is a pure function of a path and a
/// clock, which is what makes it testable without a UI thread on a platform that can run the suite.</para>
/// </summary>
public static class SettingsFileGuard
{
    /// <summary>
    /// The infix an unreadable file is copied aside under: <c>settings.json.unreadable-20260821-140502</c>.
    /// Deliberately keeps the original name as its prefix so the copy sorts beside the file it came from
    /// and reads as an artifact of this app rather than something the user has to identify.
    /// </summary>
    public const string QuarantineInfix = ".unreadable-";

    /// <summary>
    /// Reads settings.json and says which of the three states it is in. Never throws: a caller running
    /// before the logger exists has nowhere to report an exception to, which is the condition that
    /// produced the silence in the first place.
    /// </summary>
    public static SettingsFileRead Read(string settingsPath)
    {
        if (string.IsNullOrWhiteSpace(settingsPath) || !File.Exists(settingsPath))
        {
            return new SettingsFileRead(SettingsFileState.Absent, null, null, null);
        }

        string text;
        try
        {
            text = File.ReadAllText(settingsPath);
        }
        catch (Exception ex)
        {
            /* A locked, denied or truncated file is unreadable in exactly the sense that matters: we do
               not know what it says, so it must not be replaced. */
            return new SettingsFileRead(SettingsFileState.Unreadable, null, null, Describe(ex));
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            /* Reported rather than treated as absent. There is nothing to preserve in an empty file, but
               there IS something to explain: settings that were there yesterday are gone today, and an
               interrupted write is the likeliest reason. */
            return new SettingsFileRead(SettingsFileState.Unreadable, null, text, "the file is empty");
        }

        try
        {
            var node = JsonNode.Parse(text);
            if (node is JsonObject root)
            {
                return new SettingsFileRead(SettingsFileState.Readable, root, text, null);
            }

            /* A root that parses but is not an object is routed here rather than treated as "nothing to
               merge into", because the JSON literal null is the more dangerous of the two shapes: the
               writers' old `JsonNode.Parse(...) ?? new JsonObject()` read it as an empty document and
               rewrote the file from scratch, which is the loss this class exists to prevent. */
            return new SettingsFileRead(SettingsFileState.Unreadable, null, text,
                node is null
                    ? "the file holds the JSON literal null rather than an object"
                    : string.Format(CultureInfo.InvariantCulture,
                        "the file's root is a JSON {0} rather than an object",
                        node.GetValueKind().ToString().ToLowerInvariant()));
        }
        catch (JsonException ex)
        {
            return new SettingsFileRead(SettingsFileState.Unreadable, null, text, Describe(ex));
        }
    }

    /// <summary>
    /// A one-line, actionable rendering of why the file could not be read.
    ///
    /// <para>A <see cref="JsonException"/> carries the line and byte position of the character that broke
    /// the parse, and that is the difference between "settings.json is broken" and "settings.json is broken
    /// at line 42, position 7" — the second is a minute's work with an editor, so the position is passed
    /// through rather than dropped. Both counters are zero-based on the exception and one-based here,
    /// which is what an editor's status bar shows the person who has to go and look.</para>
    /// </summary>
    public static string Describe(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);

        if (ex is JsonException json && json.LineNumber.HasValue && json.BytePositionInLine.HasValue)
        {
            return string.Format(CultureInfo.InvariantCulture, "line {0}, position {1}: {2}",
                json.LineNumber.Value + 1, json.BytePositionInLine.Value + 1, WithoutPathSuffix(json.Message));
        }

        return string.Format(CultureInfo.InvariantCulture, "{0}: {1}",
            ex.GetType().Name, WithoutPathSuffix(ex.Message));
    }

    /// <summary>
    /// Copies an unreadable settings.json aside and returns the path it landed on, or null if no copy
    /// could be made.
    ///
    /// <para>Copy, not move. The original is the only surviving record of what the user configured and the
    /// write that follows can itself fail, so the worst case here has to be a duplicate rather than a
    /// hole.</para>
    /// </summary>
    public static string? Quarantine(string settingsPath, DateTime timestamp)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(settingsPath) || !File.Exists(settingsPath))
            {
                return null;
            }

            var stamp = timestamp.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var target = settingsPath + QuarantineInfix + stamp;

            /* A second save inside the same second must not overwrite the first save's copy — that would
               destroy precisely the bytes this method exists to keep. */
            for (var attempt = 2; File.Exists(target) && attempt <= 100; attempt++)
            {
                target = string.Format(CultureInfo.InvariantCulture, "{0}{1}{2}-{3}",
                    settingsPath, QuarantineInfix, stamp, attempt);
            }

            if (File.Exists(target))
            {
                return null;
            }

            File.Copy(settingsPath, target);
            return target;
        }
        catch (Exception)
        {
            /* Reported as "no copy was made" rather than thrown, so the caller decides what to do about
               it. The caller's answer is to leave the file alone. */
            return null;
        }
    }

    /// <summary>
    /// The JSON object a settings.json writer should merge into, and what had to be done to get it.
    ///
    /// <para>This is not just "parse it, or start fresh", and the difference is the whole point. Every Save
    /// in Lite is a read-modify-write of the entire document, so the read in front of it decides whether a
    /// Save is a merge or a replacement. When the file is present but unparseable, "start fresh" means the
    /// next Save — including one the user never thought of as a save, like collapsing a sidebar group —
    /// replaces a document whose contents were never understood, and the real configuration is gone for
    /// good. So the unreadable file is copied aside first, and the caller is told whether that copy
    /// exists.</para>
    /// </summary>
    public static SettingsWriteRoot RootForWrite(string settingsPath, DateTime timestamp)
    {
        var read = Read(settingsPath);

        if (read.State == SettingsFileState.Readable && read.Root is not null)
        {
            return new SettingsWriteRoot(read.Root, null, null);
        }

        if (read.State == SettingsFileState.Absent)
        {
            return new SettingsWriteRoot(new JsonObject(), null, null);
        }

        return new SettingsWriteRoot(new JsonObject(), read.Problem, Quarantine(settingsPath, timestamp));
    }

    /* System.Text.Json appends " Path: $ | LineNumber: 41 | BytePositionInLine: 6." to its parse messages.
       Describe has already given the caller the line and position in the form a human reads, so the suffix
       is cut rather than repeated. Cutting on a marker a localized build may not emit is safe: the whole
       message survives when the marker is absent. */
    private static string WithoutPathSuffix(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return message;
        }

        var marker = message.IndexOf(" Path: ", StringComparison.Ordinal);
        return marker < 0 ? message : message.Substring(0, marker);
    }
}
