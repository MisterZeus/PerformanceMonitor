/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Media;
using DuckDB.NET.Data;

namespace PerformanceMonitorLite.Services;

public partial class LocalDataService
{
    /// <summary>
    /// Gets a summary of server health for the overview dashboard.
    /// </summary>
    public async Task<ServerSummaryItem?> GetServerSummaryAsync(int serverId, string displayName)
    {
        using var connection = await OpenConnectionAsync();

        double? cpuPercent = null;
        double? otherProcessCpuPercent = null;
        double? memoryMb = null;
        int blockingCount = 0;
        int deadlockCount = 0;
        DateTime? lastCollection = null;

        /* Latest CPU — read both SQL Server CPU and other-process CPU so the UI can surface
           total non-idle CPU alongside the SQL-only number. */
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = @"
SELECT sqlserver_cpu_utilization, other_process_cpu_utilization, sample_time
FROM v_cpu_utilization_stats
WHERE server_id = $1
ORDER BY sample_time DESC
LIMIT 1";
            cmd.Parameters.Add(new DuckDBParameter { Value = serverId });
            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                cpuPercent = reader.IsDBNull(0) ? null : ToDouble(reader.GetValue(0));
                otherProcessCpuPercent = reader.IsDBNull(1) ? null : ToDouble(reader.GetValue(1));
                lastCollection = reader.IsDBNull(2) ? null : reader.GetDateTime(2);
            }
        }

        /* Latest SQL Memory */
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = @"
SELECT total_server_memory_mb
FROM v_memory_stats
WHERE server_id = $1
ORDER BY collection_time DESC
LIMIT 1";
            cmd.Parameters.Add(new DuckDBParameter { Value = serverId });
            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                memoryMb = reader.IsDBNull(0) ? null : ToDouble(reader.GetValue(0));
            }
        }

        /* Blocking count in last hour - uses XE blocked process reports */
        using (var cmd = connection.CreateCommand())
        {
            /* Prefer the blocked-process-report; fall back to the always-on DMV snapshot (AWS RDS). */
            cmd.CommandText = @"
SELECT COALESCE(NULLIF(
    (SELECT COUNT(*) FROM v_blocked_process_reports WHERE server_id = $1 AND event_time >= $2), 0),
    (SELECT COUNT(*) FROM v_dmv_blocking_snapshots WHERE server_id = $1 AND event_time >= $2))";
            cmd.Parameters.Add(new DuckDBParameter { Value = serverId });
            cmd.Parameters.Add(new DuckDBParameter { Value = DateTime.UtcNow.AddHours(-1) });
            var result = await cmd.ExecuteScalarAsync();
            blockingCount = result != null ? Convert.ToInt32(result) : 0;
        }

        /* Deadlock count in last hour */
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = @"
SELECT COUNT(*)
FROM v_deadlocks
WHERE server_id = $1
AND   deadlock_time >= $2";
            cmd.Parameters.Add(new DuckDBParameter { Value = serverId });
            cmd.Parameters.Add(new DuckDBParameter { Value = DateTime.UtcNow.AddHours(-1) });
            var result = await cmd.ExecuteScalarAsync();
            deadlockCount = result != null ? Convert.ToInt32(result) : 0;
        }

        /* Last collection time from collection_log */
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = @"
SELECT MAX(collection_time)
FROM v_collection_log
WHERE server_id = $1";
            cmd.Parameters.Add(new DuckDBParameter { Value = serverId });
            var result = await cmd.ExecuteScalarAsync();
            if (result != null && result != DBNull.Value)
            {
                lastCollection = Convert.ToDateTime(result);
            }
        }

        return new ServerSummaryItem
        {
            DisplayName = displayName,
            ServerId = serverId,
            CpuPercent = cpuPercent,
            OtherProcessCpuPercent = otherProcessCpuPercent,
            MemoryMb = memoryMb,
            BlockingCount = blockingCount,
            DeadlockCount = deadlockCount,
            LastCollectionTime = lastCollection
        };
    }
}

/// <summary>One tag pill on an Overview card (#2020 2b-i): the tag name plus the brushes to render it,
/// resolved once from the stored colour via <see cref="TagColorBrushes"/> (neutral when the tag has no
/// colour). The Lite twin of the viewer's ServerTagPill; the constant light label reads in either theme.</summary>
public sealed class ServerTagPill
{
    public ServerTagPill(string name, string? colour)
    {
        Name = name;
        Fill = TagColorBrushes.Fill(colour);
    }

    public string Name { get; }

    public System.Windows.Media.Brush Fill { get; }

    public System.Windows.Media.Brush TextBrush => TagColorBrushes.PillText;
}

/// <summary>
/// The Overview card's status-line state, as a VALUE rather than a rendered string. The word on the card
/// (<see cref="ServerSummaryItem.StatusDisplay"/>), the colour it is painted
/// (<see cref="ServerSummaryItem.StatusBrush"/>), the card's border
/// (<see cref="ServerSummaryItem.CardBorderBrush"/>), the offline overlay
/// (<see cref="ServerSummaryItem.IsOffline"/>) and the first line of its tooltip
/// (<see cref="ServerSummaryItem.StatusTooltip"/>) are all renderings OF THIS — the
/// (<c>IsOnline</c>, <c>HasCollectorErrors</c>) pair is read here and nowhere else, so no two of them can land
/// on different answers for the same card. The Darling viewer's twin (<c>ServerCardStatus</c> there) exists
/// because two review passes on #2429 each found a flag combination where two independent readings disagreed;
/// the word and the colour here already carried their own copy of the ladder, and the tooltip would have been
/// a third.
///
/// <para>Review on #2451 found the fifth: <c>CardBorderBrush</c> read <c>IsOffline</c> and
/// <c>HasCollectorErrors</c> raw, agreeing with the status word by coincidence rather than by construction.
/// It disagreed on one pair already — an unchecked card carrying a collector-error marker drew the amber
/// "collectors failing" border while its word read "Unknown". The Overview loader only sets that marker when
/// the connection check SUCCEEDED, so the pair is unreached in practice, which is exactly the argument for
/// making it unrepresentable rather than leaving it to a caller to keep avoiding.</para>
///
/// <para><b>The amber state is NOT the viewer's.</b> The viewer derives its status from collection freshness
/// and calls this member <c>Stale</c>; Lite's <c>IsOnline</c> comes from a live connection check and
/// <c>HasCollectorErrors</c> from <c>ErroringCollectors > 0</c> — collectors that are actually failing. Same
/// amber, same word "Warning", different cause. That is precisely why the tooltip has to say which one it is
/// rather than leaving the reader to infer it from a colour.</para>
/// </summary>
public enum ServerCardStatus
{
    /// <summary>The last connection check succeeded and no collector is erroring.</summary>
    Online,

    /// <summary>Connected, but one or more collectors have consecutive errors — the card's amber "Warning".
    /// Nothing to do with the metric rows, which is the whole reason it needs explaining.</summary>
    CollectorErrors,

    /// <summary>The last connection check failed — the card's red offline overlay.</summary>
    Offline,

    /// <summary>The server has not been connection-checked yet (<c>IsOnline</c> is null): a card built before
    /// the first check completed, not a dead server.</summary>
    Unknown,
}

public class ServerSummaryItem
{
    public string DisplayName { get; set; } = "";
    public string ServerName { get; set; } = "";
    public int ServerId { get; set; }
    public bool? IsOnline { get; set; }

    /// <summary>True when a whole-server alert silence is active for this server (#2031) — drives the card's
    /// muted-bell, mirroring the sidebar row. Stamped by the Overview loader and the silence-indicator
    /// refresh; the card rebinds on a real flip.</summary>
    public bool IsSilenced { get; set; }

    /// <summary>The server's tags as coloured pills, empty when it has none. Stamped by the Overview loader
    /// from the loaded tag list, so it survives a re-sort (which reuses these item instances).</summary>
    public System.Collections.Generic.IReadOnlyList<ServerTagPill> TagPills { get; set; } =
        System.Array.Empty<ServerTagPill>();
    /// <summary>True when the server is reachable but one or more collectors have consecutive errors.</summary>
    public bool HasCollectorErrors { get; set; }
    /// <summary>SQL Server scheduler ProcessUtilization from sys.dm_os_ring_buffers. NULL on Azure SQL DB.</summary>
    public double? CpuPercent { get; set; }
    /// <summary>Non-SQL-Server CPU on the host (computed as 100 - SystemIdle - ProcessUtilization). NULL on Azure SQL DB.</summary>
    public double? OtherProcessCpuPercent { get; set; }
    /// <summary>Total non-idle CPU on the host = sql_server + other_process. Tracks closer to OS user+system counters.</summary>
    public double? TotalCpuPercent =>
        CpuPercent.HasValue ? CpuPercent.Value + (OtherProcessCpuPercent ?? 0) : null;
    /// <summary>The CPU value the alert evaluator and headline display use. Driven by App.AlertCpuMode.</summary>
    public double? CpuPercentForAlert =>
        App.AlertCpuMode == CpuAlertMode.Total ? (TotalCpuPercent ?? CpuPercent) : CpuPercent;
    public double? MemoryMb { get; set; }
    public int BlockingCount { get; set; }
    public int DeadlockCount { get; set; }
    public DateTime? LastCollectionTime { get; set; }

    /// <summary>
    /// Headline CPU display. Shows total non-idle CPU prominently with the SQL-only number alongside,
    /// e.g. "64% (SQL 60%)". Falls back to a single number when only one value is available.
    /// </summary>
    public string CpuDisplay
    {
        get
        {
            if (!CpuPercent.HasValue) return "--";
            if (!OtherProcessCpuPercent.HasValue) return $"{CpuPercent:F0}%";
            return $"{TotalCpuPercent:F0}% (SQL {CpuPercent:F0}%)";
        }
    }
    public string MemoryDisplay => MemoryMb.HasValue ? $"{MemoryMb / 1024.0:F1} GB" : "--";
    public string BlockingDisplay => BlockingCount > 0 ? BlockingCount.ToString() : "0";
    public string DeadlockDisplay => DeadlockCount > 0 ? DeadlockCount.ToString() : "0";
    public string LastCollectionDisplay => LastCollectionTime.HasValue ? ServerTimeHelper.FormatServerTime(LastCollectionTime, "HH:mm:ss") : "Never";

    /* Connection status. This is the ONE place the (IsOnline, HasCollectorErrors) pair is read; the word, the
       colour and the tooltip's first line all render the result. See ServerCardStatus. */
    public ServerCardStatus CardStatus => IsOnline switch
    {
        true when HasCollectorErrors => ServerCardStatus.CollectorErrors,
        true => ServerCardStatus.Online,
        false => ServerCardStatus.Offline,
        _ => ServerCardStatus.Unknown
    };

    public string StatusDisplay => CardStatus switch
    {
        ServerCardStatus.CollectorErrors => "Warning",
        ServerCardStatus.Online => "Online",
        ServerCardStatus.Offline => "Offline",
        _ => "Unknown"
    };
    public SolidColorBrush StatusBrush => MakeBrush(CardStatus switch
    {
        ServerCardStatus.CollectorErrors => "#FFD54F",  // amber — connected but collectors failing
        ServerCardStatus.Online => "#81C784",
        ServerCardStatus.Offline => "#E57373",
        _ => "#888888"
    });
    public bool IsOffline => CardStatus == ServerCardStatus.Offline;

    /* ── Per-row concern gates ───────────────────────────────────────────────────────────────────────
       Each metric row's "this is not green" test, evaluated ONCE. The row's own brush reads it and so does
       StatusReason, which is what makes the tooltip incapable of naming a metric whose row is green — or of
       staying silent about one that is not. A tooltip built from an independent severity calculation would
       lose exactly that property, and the one place it would disagree is a card the reader is staring at.

       Note the gates follow the ROW, not the card border: the CPU row goes amber at 50 while the border only
       escalates at 80. The reason names rows, so it uses the row's threshold. */
    private bool CpuIsElevated => CpuPercentForAlert >= 50;
    private bool CpuIsCritical => CpuPercentForAlert >= 80;
    private bool BlockingIsElevated => BlockingCount > 0;
    private bool DeadlocksAreElevated => DeadlockCount > 0;

    /* Color coding */
    public SolidColorBrush CpuBrush => MakeBrush(CpuIsCritical ? "#E57373" : CpuIsElevated ? "#FFB74D" : "#81C784");
    public SolidColorBrush BlockingBrush => MakeBrush(BlockingIsElevated ? "#FFB74D" : "#81C784");
    public SolidColorBrush DeadlockBrush => MakeBrush(DeadlocksAreElevated ? "#E57373" : "#81C784");
    /* The border ranks the same states the status word names, so it reads the SAME discriminant rather than
       the flags behind it — see ServerCardStatus for the pair the raw reads disagreed on. The precedence is
       unchanged: a dark server first, then the metric rows worst-first, then failing collectors. */
    public SolidColorBrush CardBorderBrush => MakeBrush(
        CardStatus == ServerCardStatus.Offline ? "#E57373" :
        DeadlocksAreElevated ? "#E57373" :
        BlockingIsElevated ? "#FFB74D" :
        CpuIsCritical ? "#FFB74D" :
        CardStatus == ServerCardStatus.CollectorErrors ? "#FFD54F" :   // amber border when collectors are failing
        "#2a2d35");

    /* ── The card explains itself (#2437 / #2422) ────────────────────────────────────────────────────
       Reported against the Darling viewer and fixed there in #2429; Lite's card had the identical bare
       status binding. A reader saw a word and a colour and had to scan the metric rows guessing which one
       the card meant — once per card, on every card.

       Lite's version has one more thing to say than the viewer's, because Lite's status word is a CONNECTION
       word, not a health band: a card with 96% CPU and two deadlocks still says "Online" in green while its
       border is red, and a card saying "Warning" in amber is reporting failing COLLECTORS and nothing about
       the metrics at all. Fusing the two into one clause would reproduce the ambiguity in prose, so the
       tooltip keeps them on separate lines: what the status word means, then what the rows say. */

    /// <summary>What the status word means, in words — the tooltip's first line. Switches on
    /// <see cref="CardStatus"/>, the same discriminant <see cref="StatusDisplay"/> renders, so a tooltip that
    /// hangs off a word can never contradict it.</summary>
    private string StatusHeadline => CardStatus switch
    {
        ServerCardStatus.CollectorErrors => "Warning — one or more collectors are failing on this server",
        ServerCardStatus.Online => "Online — the last connection check succeeded",
        ServerCardStatus.Offline => "Offline — the last connection check failed",
        _ => "Unknown — this server has not been connection-checked yet",
    };

    /// <summary>
    /// The metric rows that are not green, in the card's OWN display strings — "CPU 96% (SQL 90%), Deadlocks 2".
    /// Assembled from the displays rather than from the underlying numbers so the sentence and the rows cannot
    /// render differently, and gated on the same predicates the row brushes use so it cannot name a different
    /// set of rows than the ones that are coloured. Empty when every row is green.
    ///
    /// <para>Memory is absent deliberately: the Memory row carries no severity brush on this card, so there is
    /// no band to report. Inventing a threshold here would be the one thing this property exists to prevent —
    /// a tooltip asserting something the rows underneath it do not.</para>
    /// </summary>
    public string StatusReason
    {
        get
        {
            var parts = new List<string>();

            if (CpuIsElevated)
            {
                parts.Add("CPU " + CpuDisplay);
            }

            if (BlockingIsElevated)
            {
                parts.Add("Blocking " + BlockingDisplay);
            }

            if (DeadlocksAreElevated)
            {
                parts.Add("Deadlocks " + DeadlockDisplay);
            }

            return string.Join(", ", parts);
        }
    }

    /// <summary>
    /// The Overview card's status tooltip (#2437, answering #2422 on Lite's surface): what the status word
    /// means, what the metric rows say, and what to do next.
    ///
    /// <para>The metric line is omitted on an Offline card. The card draws a dimming overlay across those rows
    /// precisely because the numbers under it are the last ones collected before the server went dark, so
    /// demanding attention for them would contradict the card while the reader is looking at it.</para>
    /// </summary>
    public string StatusTooltip
    {
        get
        {
            var lines = new List<string> { StatusHeadline };

            if (CardStatus != ServerCardStatus.Offline)
            {
                var reason = StatusReason;
                lines.Add(reason.Length > 0
                    ? "Needs attention: " + reason
                    : "Every metric on this card is inside its threshold");
            }

            lines.Add(CardTooltipAction);

            return string.Join("\n", lines);
        }
    }

    /// <summary>The line every card tooltip ends on — the gesture the card actually supports.
    /// <c>OverviewCard_MouseLeftButtonDown</c> acts only on <c>ClickCount == 2</c>, so naming a single click
    /// would be naming a no-op. The viewer's card tooltip ends on this same sentence, so a reader moving
    /// between the two apps is told the same thing in the same words.</summary>
    private const string CardTooltipAction = "Double-click the card to open this server's tab";

    private static SolidColorBrush MakeBrush(string hex)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
    public bool HasAlerts => BlockingCount > 0 || DeadlockCount > 0;
}
