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
using System.Linq;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PerformanceMonitor.Common;

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
/// One top-level member of a settings file whose VALUE the deserializer could not read, and what was wrong
/// with it (#2456). Carried rather than thrown, because the whole point is that the reader gets to name it.
/// </summary>
public readonly record struct SettingsMemberProblem(string Member, string Problem)
{
    /// <summary>One line, ready to put in a log or a dialog.</summary>
    public override string ToString() => $"{Member} ({Problem})";
}

/// <summary>
/// The outcome of <see cref="SettingsFileGuard.ReadObject{T}"/>, for a store that round-trips a whole
/// object rather than merging a document key by key.
///
/// <para><c>Value</c> is non-null for <see cref="SettingsFileState.Readable"/> and for a file that was read
/// after DROPPING members the deserializer could not understand — in which case the state is still
/// <see cref="SettingsFileState.Unreadable"/>, because the file as written is exactly that, and the next
/// save must still copy it aside before replacing it. <c>UnreadableMembers</c> is what separates the two:
/// empty (or null) means nothing in the file was usable, and the caller's own defaults are the whole
/// answer.</para>
/// </summary>
public readonly record struct SettingsObjectRead<T>(
    SettingsFileState State,
    T? Value,
    string? Problem,
    IReadOnlyList<SettingsMemberProblem>? UnreadableMembers = null) where T : class;

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
/// The outcome of <see cref="SettingsFileGuard.PermitReplace"/>: whether a store that REPLACES the whole
/// file is allowed to go ahead. <c>Allowed</c> is false in exactly one situation — the file could not be
/// read AND could not be copied aside — and it must be honoured, because the write it is refusing would
/// be the last event in the life of a configuration nobody ever managed to read.
/// </summary>
public readonly record struct SettingsReplacePermit(
    bool Allowed,
    string? Problem,
    string? QuarantinedTo);

/// <summary>
/// The one place that decides whether a JSON settings file can be read, and what to do about it when it
/// cannot (#2425, #2434).
///
/// <para>Kept free of WPF, of the logger and of any process-wide state on purpose. Every decision that
/// matters here — absent versus unreadable, what the user is told about a parse error, whether a Save is a
/// merge or a replacement, whether the old file is preserved first — is a pure function of a path and a
/// clock, which is what makes it testable without a UI thread on a platform that can run the suite.</para>
///
/// <para>It lives in Common rather than in Lite because #2434 found the identical defect in the Darling
/// viewer's two stores, and the half that matters — classify, then copy aside before replacing — is the
/// half that is genuinely common. What differs is only the shape of the document above it: Lite merges a
/// <see cref="JsonObject"/> key by key (<see cref="RootForWrite"/>), while the viewer round-trips a whole
/// object through <see cref="JsonSerializer"/> and REPLACES the file (<see cref="ReadObject{T}"/> and
/// <see cref="PermitReplace"/>). Mirroring this class into the viewer instead would have meant two copies
/// of the quarantine rule, which is exactly the drift that produced the second instance.</para>
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
        var text = ReadText(settingsPath, out var state, out var problem);
        if (state != SettingsFileState.Readable)
        {
            return new SettingsFileRead(state, null, text, problem);
        }

        try
        {
            var node = JsonNode.Parse(text!);
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

    /// <summary>
    /// <see cref="Read"/> for a store whose file is one serialized value rather than a document to merge
    /// into: classifies the file, and when there is text to work with deserializes it to
    /// <typeparamref name="T"/>.
    ///
    /// <para>The typed deserialize is inside the guard rather than in front of it because it can fail on a
    /// file that is perfectly good JSON — one key holding a value of the wrong shape, a quoted number where
    /// an int belongs. That file is unreadable in every sense the caller cares about, and the caller cares
    /// because the next Save replaces it.</para>
    ///
    /// <para>Deliberately does NOT apply <see cref="Read"/>'s "the root must be an object" rule, and the
    /// distinction is load-bearing rather than pedantic: that rule exists because a merge has nothing to
    /// merge into otherwise, and the viewer's server registry is a legitimate JSON ARRAY. Borrowing the
    /// rule here would have declared every healthy registry unreadable and quarantined a copy of it on
    /// every add, edit and favourite toggle. The typed deserialize is the right judge of whether the file
    /// says what this caller reads — for a settings object an array still fails, and for a
    /// <c>List&lt;T&gt;</c> an object still fails.</para>
    ///
    /// <para>When the deserialize fails on ONE member's value rather than on the document, that member is
    /// named, dropped, and the read is attempted again — see
    /// <see cref="DeserializeWithMemberRecovery{T}"/> for why that is the shape rather than per-property
    /// reads (#2456).</para>
    /// </summary>
    public static SettingsObjectRead<T> ReadObject<T>(string settingsPath, JsonSerializerOptions? options = null)
        where T : class
    {
        var text = ReadText(settingsPath, out var state, out var problem);
        if (state != SettingsFileState.Readable)
        {
            return new SettingsObjectRead<T>(state, null, problem);
        }

        return DeserializeWithMemberRecovery<T>(text!, options);
    }

    /// <summary>
    /// A top-level member name in a <see cref="JsonException.Path"/>: <c>$.AlertCpuThreshold</c> and
    /// <c>$.AlertExcludedDatabases[1]</c> both yield <c>AlertCpuThreshold</c> / <c>AlertExcludedDatabases</c>,
    /// while <c>$</c> and <c>$[3].DisplayName</c> yield nothing. Anchored and deliberately narrow — see
    /// <see cref="DeserializeWithMemberRecovery{T}"/>.
    /// </summary>
    private static readonly Regex s_topLevelMember =
        new(@"^\$\.([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.CultureInvariant);

    /// <summary>A file cannot lose more members than it has; the cap is a termination guard, not a policy.</summary>
    private const int MaxDroppedMembers = 500;

    /// <summary>
    /// Deserialize <typeparamref name="T"/>, and when the failure is one member's VALUE rather than the
    /// document, name that member, drop it, and try again — so a badly-shaped setting costs exactly its own
    /// setting and the read can say which ones it lost (#2456).
    ///
    /// <para><b>Why this shape and not per-property reads.</b> #2444 gave Lite a per-KEY reader, which works
    /// there because Lite's settings.json is built key by key on BOTH sides: #2441 made every writer mutate
    /// one <see cref="JsonObject"/>, so a per-key read is symmetric with a per-key write. These files are a
    /// whole-object round trip on both sides, and converting only the READ half to hand-written properties
    /// would break that symmetry: a property added to the settings class would still be serialized on save
    /// and would silently never be read back. That is a worse defect than the one being fixed, and an
    /// invisible one. Dropping the named member and re-running the SAME deserialize keeps the round trip
    /// intact, so a new property is covered the day it is added.</para>
    ///
    /// <para><b>Why it is not two readers.</b> #2213 spent a review round on a two-pass classify where the
    /// second pass judged the file by a different standard from the first. Every attempt here is
    /// <see cref="JsonSerializer.Deserialize{T}(string, JsonSerializerOptions)"/> with the caller's own
    /// options over the caller's own type. The only thing that changes between attempts is that one member
    /// is gone, so the two passes cannot disagree about what "unreadable" means — there is only one judge.</para>
    ///
    /// <para><b>What it deliberately will not recover.</b> Only a top-level member of a root JSON OBJECT.
    /// The viewer's server registry is a root ARRAY, and a bad element there has a path of
    /// <c>$[3]</c> — dropping it would silently delete a monitored server from the operator's registry,
    /// which is the data loss #2434 exists to prevent, dressed up as a repair. So an array-rooted file keeps
    /// its all-or-nothing behaviour and says so, and
    /// <c>ReadObject_LeavesAnArrayRootedRegistryAllOrNothing</c> is the control for exactly that mistake.</para>
    ///
    /// <para>The state stays <see cref="SettingsFileState.Unreadable"/> for a partially-recovered file, on
    /// purpose. The file on disk is still the one that could not be read, so <see cref="PermitReplace{T}"/>
    /// must still copy it aside before anything replaces it — the recovered values are in memory, and the
    /// dropped members' original text exists nowhere else.</para>
    /// </summary>
    internal static SettingsObjectRead<T> DeserializeWithMemberRecovery<T>(string text, JsonSerializerOptions? options)
        where T : class
    {
        List<SettingsMemberProblem>? dropped = null;
        var current = text;

        for (var attempt = 0; attempt <= MaxDroppedMembers; attempt++)
        {
            JsonException failure;
            try
            {
                var value = JsonSerializer.Deserialize<T>(current, options);

                /* The JSON literal null parses and deserializes to null. Reported, because it is the shape
                   that reads as "nothing was configured" to a caller that only null-checks, and the
                   consequence of believing that is a file replaced from defaults. */
                if (value is null)
                {
                    return new SettingsObjectRead<T>(SettingsFileState.Unreadable, null,
                        "the file holds the JSON literal null rather than the settings it should", dropped);
                }

                return dropped is null
                    ? new SettingsObjectRead<T>(SettingsFileState.Readable, value, null)
                    : new SettingsObjectRead<T>(SettingsFileState.Unreadable, value, Summarize(dropped), dropped);
            }
            catch (JsonException ex)
            {
                failure = ex;
            }
            catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
            {
                /* Not a member fault and carries no path to attribute one with. */
                return new SettingsObjectRead<T>(SettingsFileState.Unreadable, null, Describe(ex), dropped);
            }

            var member = TopLevelMember(failure);
            var without = member is null ? null : WithoutMember(current, member);

            if (member is null || without is null)
            {
                return new SettingsObjectRead<T>(SettingsFileState.Unreadable, null, Describe(failure), dropped);
            }

            (dropped ??= new List<SettingsMemberProblem>()).Add(new SettingsMemberProblem(member, Describe(failure)));
            current = without;
        }

        /* Unreachable for any real file — every pass removes a member that was there — but a loop that
           edits its own input gets a bound rather than a proof. */
        return new SettingsObjectRead<T>(SettingsFileState.Unreadable, null,
            "the file holds more unreadable settings than this reader will drop", dropped);
    }

    /// <summary>The member a <see cref="JsonException"/> stopped on, or null when it stopped on the document
    /// itself (<c>$</c>) or somewhere this reader will not edit.</summary>
    private static string? TopLevelMember(JsonException failure)
    {
        var path = failure.Path;
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var match = s_topLevelMember.Match(path);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// The same JSON with one top-level member removed, or null when the root is not an object, the member
    /// is not actually there, or the text will not re-parse. The existence check is what makes the retry
    /// loop terminate: a pass that removed nothing would fail identically forever.
    /// </summary>
    private static string? WithoutMember(string text, string member)
    {
        try
        {
            if (JsonNode.Parse(text) is not JsonObject root || !root.Remove(member))
            {
                return null;
            }

            return root.ToJsonString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The one-line <c>Problem</c> for a partially-recovered file. Names the members rather than counting
    /// them: a count sends someone to proofread the whole file, which is the message #2444 replaced.
    /// </summary>
    private static string Summarize(List<SettingsMemberProblem> dropped) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "{0} setting{1} could not be read and {2} at {3} default: {4}",
            dropped.Count,
            dropped.Count == 1 ? "" : "s",
            dropped.Count == 1 ? "is" : "are",
            dropped.Count == 1 ? "its" : "their",
            string.Join(", ", dropped.Select(d => d.ToString())));

    /// <summary>
    /// <see cref="RootForWrite"/>'s twin for a store that REPLACES the file instead of merging into it.
    /// There is no document to hand back — the caller already holds the object it means to serialize — so
    /// the only question left is whether writing it is allowed to destroy what is there now.
    ///
    /// <para>An absent or readable file is a plain yes: nothing is lost, because a first run has nothing
    /// to lose and a readable file's contents are what the caller loaded in the first place. An unreadable
    /// one is copied aside first, and the answer is yes only if that copy exists. A caller that ignores a
    /// no and writes anyway has just done the thing this whole class exists to prevent.</para>
    /// </summary>
    public static SettingsReplacePermit PermitReplace<T>(
        string settingsPath,
        DateTime timestamp,
        JsonSerializerOptions? options = null)
        where T : class
    {
        /* Generic in T, and re-reading rather than being told, so that the question asked here is exactly
           the question the store's own Load asks: can this file still be read as what is about to replace
           it? A permit that judged the file by some other standard would either quarantine healthy files
           or wave through the destruction of one it never understood. */
        var read = ReadObject<T>(settingsPath, options);

        if (read.State != SettingsFileState.Unreadable)
        {
            return new SettingsReplacePermit(true, null, null);
        }

        var quarantinedTo = Quarantine(settingsPath, timestamp);
        return new SettingsReplacePermit(quarantinedTo is not null, read.Problem, quarantinedTo);
    }

    /// <summary>
    /// Everything both readers agree on before the JSON matters: no file is Absent and silent, a file that
    /// will not come off disk is Unreadable, and an empty one is Unreadable rather than Absent — there is
    /// nothing to preserve in an empty file but there IS something to explain, since the settings that were
    /// there yesterday are gone today and an interrupted write is the likeliest reason.
    /// </summary>
    private static string? ReadText(string settingsPath, out SettingsFileState state, out string? problem)
    {
        if (string.IsNullOrWhiteSpace(settingsPath) || !File.Exists(settingsPath))
        {
            state = SettingsFileState.Absent;
            problem = null;
            return null;
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
            state = SettingsFileState.Unreadable;
            problem = Describe(ex);
            return null;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            state = SettingsFileState.Unreadable;
            problem = "the file is empty";
            return text;
        }

        state = SettingsFileState.Readable;
        problem = null;
        return text;
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
