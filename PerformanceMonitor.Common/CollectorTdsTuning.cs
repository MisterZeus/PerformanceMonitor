/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

namespace PerformanceMonitor.Common;

/// <summary>
/// TDS-level transport settings both apps apply when they build a monitored-server connection string
/// (#2164). One definition so Lite and Darling cannot drift apart on how they talk to a monitored
/// instance — the two builders are otherwise independent code.
/// </summary>
public static class CollectorTdsTuning
{
    /// <summary>
    /// The TDS packet size, in bytes, for monitored-server connections. Neither app used to set this, so
    /// every connection ran at the driver's 8 KB default.
    ///
    /// <para><b>Why it matters here.</b> The heavy collectors read rows carrying two
    /// <c>nvarchar(max)</c> columns — query text and execution-plan XML — and the client reads each row
    /// whole before the loop sees it. Measured on a cross-region production fleet, effective drain
    /// throughput tracked LOB size rather than total bytes: a database averaging ~180 KB per row moved
    /// 256 KB/s while one averaging ~41 KB per row moved 1,080 KB/s over the same link, same code. That
    /// inverse relationship is per-packet overhead, and 8 KB packets mean a 180 KB plan costs ~22 packets
    /// where 32 KB packets cost ~6.</para>
    ///
    /// <para>32 KB is the largest value SQL Server negotiates (the protocol maximum is 32,767; the driver
    /// accepts 512–32,768 and both ends agree down to the smaller). Chosen over an intermediate value
    /// because the payloads at issue are hundreds of KB — the whole point is fewer, larger packets — and
    /// because a server that dislikes it negotiates down rather than failing.</para>
    ///
    /// <para><b>Cost of being wrong:</b> a larger packet size raises per-connection network-buffer memory
    /// on both ends. At the fleet's connection count (one per server per sweep, bounded by the sweep
    /// width) that is kilobytes, not megabytes — cheap enough that it is not worth a knob until someone
    /// shows it hurts. This is measured against a fleet where it should help most; if the numbers say
    /// otherwise it comes back out, and the comment records what evidence would justify that.</para>
    /// </summary>
    public const int MonitoredServerPacketSize = 32768;
}
