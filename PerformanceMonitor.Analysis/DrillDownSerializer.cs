/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Text.Json;

namespace PerformanceMonitor.Analysis;

/// <summary>
/// Serializes a finding's drill-down for persistence (#2060) — the <c>remediation_action_json</c>
/// pattern (recommendations rebuild D2) applied to the drill-down itself: the drill-down is built
/// post-enrich on the WRITE path and was previously ephemeral, so <c>get_analysis_findings</c>
/// could name "13 plans showed the pattern" while no surface could enumerate them; a triaging
/// agent had to re-derive the list the engine had already computed (the #2054 fleet-chain triage
/// did exactly that, through get_top_queries_by_cpu). Persisting a CAPPED copy closes that gap
/// without letting a pathological drill-down bloat the findings table.
///
/// <para><b>Caps, applied at write time, never silently</b>: each section keeps its first
/// <see cref="MaxRowsPerSection"/> rows (drill-down collectors emit worst-first, so the head is
/// the signal), and the whole serialized payload is bounded by <see cref="MaxSerializedBytes"/> —
/// a payload still over the byte cap after row-capping drops whole sections from the END and
/// says so. Any truncation adds a <see cref="TruncationNoteKey"/> entry naming what was dropped,
/// so a capped read never masquerades as the complete evidence (the no-silent-caps rule).</para>
///
/// <para><b>Round-trip shape</b>: values arrive as anonymous objects (or lists of them) behind
/// <c>object</c> and come back as <see cref="JsonElement"/>s — the exact tolerance the alert
/// path's <c>FindingMessageFormatter</c> already has for drill-down values, and System.Text.Json
/// serializes <see cref="JsonElement"/> natively, so both consumers (MCP envelope, alert context)
/// handle a read-back drill-down identically to a live one.</para>
/// </summary>
public static class DrillDownSerializer
{
    /// <summary>Rows kept per drill-down section — collectors emit worst-first, so the head is
    /// the evidence a responder acts on; the tail is bulk.</summary>
    public const int MaxRowsPerSection = 10;

    /// <summary>Upper bound on the persisted JSON, after row-capping — one finding row must never
    /// carry megabytes of drill-down (query texts ride in these rows).</summary>
    public const int MaxSerializedBytes = 64 * 1024;

    /// <summary>The key added to a capped drill-down naming exactly what was dropped.</summary>
    public const string TruncationNoteKey = "_truncation_note";

    /// <summary>
    /// Serializes a drill-down with the caps applied. Null/empty in, null out (the column stays
    /// NULL — an absent drill-down is honest, not an empty object). Never throws: an unserializable
    /// section is dropped and named in the truncation note, matching the alert formatter's
    /// skip-this-entry-keep-the-rest discipline.
    /// </summary>
    public static string? Serialize(Dictionary<string, object>? drillDown)
    {
        if (drillDown is null || drillDown.Count == 0)
        {
            return null;
        }

        var capped = new Dictionary<string, object>(StringComparer.Ordinal);
        var dropped = new List<string>();

        foreach (var (key, value) in drillDown)
        {
            if (value is null)
            {
                continue;
            }

            try
            {
                var element = JsonSerializer.SerializeToElement(value);
                if (element.ValueKind == JsonValueKind.Array && element.GetArrayLength() > MaxRowsPerSection)
                {
                    var head = new List<JsonElement>(MaxRowsPerSection);
                    foreach (var item in element.EnumerateArray())
                    {
                        head.Add(item);
                        if (head.Count == MaxRowsPerSection)
                        {
                            break;
                        }
                    }

                    capped[key] = head;
                    dropped.Add($"{key}: kept first {MaxRowsPerSection} of {element.GetArrayLength()} rows");
                }
                else
                {
                    capped[key] = element;
                }
            }
            catch (Exception)
            {
                /* Unserializable section — drop it, keep the rest, say so. */
                dropped.Add($"{key}: unserializable, dropped");
            }
        }

        if (capped.Count == 0)
        {
            return null;
        }

        if (dropped.Count > 0)
        {
            capped[TruncationNoteKey] = string.Join("; ", dropped);
        }

        var json = JsonSerializer.Serialize(capped);

        /* Still over the byte cap after row-capping: drop whole sections from the end until it
           fits, keeping the note current — worst-first ordering means the head sections are the
           ones worth keeping. */
        while (json.Length > MaxSerializedBytes && capped.Count > 1)
        {
            string? last = null;
            foreach (var key in capped.Keys)
            {
                if (!string.Equals(key, TruncationNoteKey, StringComparison.Ordinal))
                {
                    last = key;
                }
            }

            if (last is null)
            {
                break;
            }

            capped.Remove(last);
            dropped.Add($"{last}: dropped for size");
            capped[TruncationNoteKey] = string.Join("; ", dropped);
            json = JsonSerializer.Serialize(capped);
        }

        return json.Length <= MaxSerializedBytes ? json : null;
    }

    /// <summary>
    /// Deserializes a persisted drill-down back into the finding's shape — values come back as
    /// <see cref="JsonElement"/>s, which every existing drill-down consumer already tolerates.
    /// Null/blank/malformed in, null out (a corrupt column reads as "no drill-down", the
    /// conservative direction — matching the remediation-action round-trip's discipline).
    /// </summary>
    public static Dictionary<string, object>? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var element = JsonSerializer.Deserialize<JsonElement>(json);
            if (element.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                result[property.Name] = property.Value.Clone();
            }

            return result.Count > 0 ? result : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
