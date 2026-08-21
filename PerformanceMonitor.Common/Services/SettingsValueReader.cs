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
using System.Text.Json;

namespace PerformanceMonitor.Common;

/// <summary>
/// One key whose value was the wrong SHAPE, and what was wrong with it (#2444). Carried rather than thrown,
/// because the whole point is that a bad key costs its own setting and nothing else.
/// </summary>
public readonly record struct SettingsValueProblem(string Key, string Problem)
{
    /// <summary>One line, ready to put in a log or a dialog.</summary>
    public override string ToString() => $"{Key} — {Problem}";
}

/// <summary>
/// Reads typed values out of an already-parsed settings.json object, recording the keys it could not read
/// instead of throwing on the first one (#2444).
///
/// <para><b>The defect this replaces.</b> <c>App.LoadAlertSettings</c> wrapped all eighty-seven reads in one
/// <c>try</c>. A single key of the wrong shape — a quoted number, a string where a bool belongs — threw on
/// its own <c>Get*</c> call and abandoned every read AFTER it, so which settings survived depended on where
/// the bad key happened to sit in the file. One wrong value near the top cost almost everything; the same
/// value near the bottom cost almost nothing. Nothing about that was visible, and the ordering it turned on
/// is an implementation detail of the loader.</para>
///
/// <para><b>Checked by kind, not caught.</b> The same call <c>McpSettings.Load</c> already makes, for the
/// reason it gives: a <c>catch</c> around <c>GetBoolean</c> is what turned a quoted
/// <c>"true"</c> into a silently disabled endpoint. Every read below asks <c>ValueKind</c> first, so a wrong
/// shape is a recorded ANSWER rather than an exception, and the caller keeps the value it already had.
/// <c>null</c> is treated as a wrong shape too, uniformly: Lite never writes one, so a null in this file is
/// a hand-edit and is exactly the class of mistake this exists to name.</para>
///
/// <para><b>Why the method is called <c>TryGetProperty</c>.</b> That name is load-bearing, not a stylistic
/// echo of <see cref="JsonElement"/>'s. <c>SettingsSampleTests</c> (#2418) extracts the documented key list
/// by regexing <c>TryGetProperty("…"</c> literals out of <c>App.xaml.cs</c>, and requires every key it finds
/// to be documented in <c>settings.sample.json</c> and vice versa. A helper spelled any other way makes all
/// eighty-seven keys vanish from that extraction, and the sample is then free to drift exactly the way #2418
/// was filed about — which is what PR #2428 hit, and why it had to read back through
/// <see cref="JsonDocument"/>. So the call sites keep the literal in the shape the extractor already
/// understands, the extractor needs no change, and <c>KeyExtraction_SeesBothReaderShapes…</c> pins that this
/// shape really is seen rather than assuming it.</para>
/// </summary>
public sealed class SettingsReader
{
    private readonly JsonElement _root;
    private readonly List<SettingsValueProblem> _problems = new();

    /// <param name="root">The parsed settings.json object. <c>SettingsFileGuard.Read</c> has already decided
    /// that it IS an object, so a document-level fault never reaches here — everything this type reports is a
    /// single value's shape.</param>
    public SettingsReader(JsonElement root) => _root = root;

    /// <summary>Every key whose value could not be read, in the order they were met. Empty is the normal
    /// case and means every key present in the file was applied.</summary>
    public IReadOnlyList<SettingsValueProblem> Problems => _problems;

    /// <summary>
    /// The key's value, or false when the file does not carry that key at all. An absent key is NOT a
    /// problem — it is how every default works and how a settings.json written by an older version behaves.
    /// </summary>
    public bool TryGetProperty(string key, out SettingsValue value)
    {
        if (_root.TryGetProperty(key, out var element))
        {
            value = new SettingsValue(this, key, element);
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>Records one key as unreadable. Idempotent per key so a value read twice cannot report twice.</summary>
    internal void Reject(string key, string problem)
    {
        foreach (var existing in _problems)
        {
            if (string.Equals(existing.Key, key, StringComparison.Ordinal))
            {
                return;
            }
        }

        _problems.Add(new SettingsValueProblem(key, problem));
    }
}

/// <summary>
/// One key's value, which knows its own NAME — that is the whole reason this is a type rather than a bare
/// <see cref="JsonElement"/>. Every reader below takes the value to keep when the shape is wrong, so a call
/// site reads as one line and cannot forget to handle the failure: there is no failure path to forget.
///
/// <para>The clamps live here rather than at the eighty-seven call sites that used to repeat
/// <c>Math.Clamp(v.GetInt64(), …)</c> inline. That collapses the read AND makes one latent bug unreachable:
/// the old <c>(int)Math.Max(0, v.GetInt64())</c> form casts an out-of-range Int64 into an <c>int</c>, which
/// wraps — so a hand-typed 5000000000 became a negative threshold. <see cref="Int(int,int,int)"/> clamps
/// before it narrows, so the worst a huge number can do is land on the bound.</para>
/// </summary>
public readonly struct SettingsValue
{
    private readonly SettingsReader? _reader;
    private readonly string _key;

    internal SettingsValue(SettingsReader reader, string key, JsonElement element)
    {
        _reader = reader;
        _key = key;
        Element = element;
    }

    /// <summary>The raw element, for the two array-valued settings that enumerate it themselves.</summary>
    public JsonElement Element { get; }

    /// <summary>The value as a bool, or <paramref name="fallback"/> when it is not <c>true</c>/<c>false</c>.</summary>
    public bool Bool(bool fallback) =>
        Element.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? Element.GetBoolean()
            : Reject(fallback, "true or false");

    /// <summary>The value as an int, unclamped — for the settings that have never had a range.</summary>
    public int Int(int fallback) =>
        Element.ValueKind == JsonValueKind.Number && Element.TryGetInt32(out var number)
            ? number
            : Reject(fallback, "a whole number");

    /// <summary>
    /// The value as an int, clamped into <paramref name="min"/>..<paramref name="max"/>. Read as Int64 first
    /// on purpose: a number too large for an <c>int</c> is a value out of range, which the clamp is there to
    /// handle, not a value of the wrong shape.
    /// </summary>
    public int Int(int fallback, int min, int max) =>
        Element.ValueKind == JsonValueKind.Number && Element.TryGetInt64(out var number)
            ? (int)Math.Clamp(number, min, max)
            : Reject(fallback, "a whole number");

    /// <summary>The value as a long, clamped into <paramref name="min"/>..<paramref name="max"/>.</summary>
    public long Long(long fallback, long min, long max) =>
        Element.ValueKind == JsonValueKind.Number && Element.TryGetInt64(out var number)
            ? Math.Clamp(number, min, max)
            : Reject(fallback, "a whole number");

    /// <summary>The value as a double, clamped into <paramref name="min"/>..<paramref name="max"/>.</summary>
    public double Double(double fallback, double min, double max) =>
        Element.ValueKind == JsonValueKind.Number && Element.TryGetDouble(out var number)
            ? Math.Clamp(number, min, max)
            : Reject(fallback, "a number");

    /// <summary>The value as a string, or <paramref name="fallback"/> when it is not one.</summary>
    public string Text(string fallback) =>
        Element.ValueKind == JsonValueKind.String
            ? Element.GetString() ?? fallback
            : Reject(fallback, "text");

    /// <summary>
    /// The value as a string, or null when it is not one — for the settings that validate the string
    /// themselves (an enum token, a whitelist of separators). A value that is a string but not a RECOGNISED
    /// one is not reported here: it is the caller's own vocabulary, it has always been ignored silently, and
    /// widening that into a startup dialog is a different decision from this one.
    /// </summary>
    public string? TextOrNull() =>
        Element.ValueKind == JsonValueKind.String ? Element.GetString() : Reject<string?>(null, "text");

    /// <summary>Whether the value is an array, recording it when it is not.</summary>
    public bool IsArray() =>
        Element.ValueKind == JsonValueKind.Array || Reject(false, "a list");

    /// <summary>
    /// Records this key against its reader and hands back the caller's fallback, so every reader above is one
    /// expression. The message names the kind that WAS there, in the same phrasing the MCP settings loader
    /// already uses, because "it holds a JSON string" is what lets someone find the line in their file.
    /// </summary>
    private T Reject<T>(T fallback, string expected)
    {
        _reader?.Reject(
            _key,
            string.Format(
                CultureInfo.InvariantCulture,
                "holds a JSON {0} where {1} belongs",
                Element.ValueKind.ToString().ToLowerInvariant(),
                expected));

        return fallback;
    }
}
