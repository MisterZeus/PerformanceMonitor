/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Text;
using Npgsql;
using NpgsqlTypes;
using PerformanceMonitor.Collectors;

namespace PerformanceMonitor.Darling.Storage;

/// <summary>
/// Adapts the collector definitions' positional row writes onto Postgres binary COPY — Darling's
/// counterpart of Lite's DuckDB appender adapter. The host opens one binary import per
/// collection batch (<see cref="CopyCommandFor"/>), calls StartRow + writes the prefix columns,
/// then hands the writer to the definition's WritePayload; column ORDER is the contract, exactly
/// as with the appender.
/// All DateTimes in the product are UTC by convention but stored in naive `timestamp` columns
/// (mirroring Lite's DuckDB TIMESTAMP), so every DateTime is written with
/// DateTimeKind.Unspecified: since Npgsql 6.0 a Kind=Utc DateTime maps strictly to timestamptz
/// and throws InvalidCastException against `timestamp without time zone`
/// (https://www.npgsql.org/doc/types/datetime.html).
/// </summary>
public sealed class PgCollectorRowWriter : ICollectorRowWriter
{
    /// <summary>The active binary importer; the host sets this once per COPY batch.</summary>
    public NpgsqlBinaryImporter? Importer { get; set; }

    private NpgsqlBinaryImporter Target
        => Importer ?? throw new InvalidOperationException("Importer not set — open a binary import first.");

    /// <summary>Maps an engine-neutral collector column type to its binary-COPY parameter type.</summary>
    public static NpgsqlDbType DbTypeFor(CollectorColumnType type)
    {
        switch (type)
        {
            case CollectorColumnType.BigInt: return NpgsqlDbType.Bigint;
            case CollectorColumnType.Integer: return NpgsqlDbType.Integer;
            case CollectorColumnType.SmallInt: return NpgsqlDbType.Smallint;
            case CollectorColumnType.Varchar: return NpgsqlDbType.Text;
            case CollectorColumnType.Timestamp: return NpgsqlDbType.Timestamp;
            case CollectorColumnType.Double: return NpgsqlDbType.Double;
            case CollectorColumnType.Decimal: return NpgsqlDbType.Numeric;
            case CollectorColumnType.Boolean: return NpgsqlDbType.Boolean;
            default:
                throw new ArgumentOutOfRangeException(nameof(type), type, "Unmapped collector column type");
        }
    }

    /// <summary>
    /// The binary COPY command for one collector's destination table, prefix columns first in
    /// the exact order the host writes them (id column omitted for running_jobs' no-id prefix).
    /// </summary>
    public static string CopyCommandFor(ICollectorSchemaInfo schema)
    {
        if (schema is null)
        {
            throw new ArgumentNullException(nameof(schema));
        }

        var sb = new StringBuilder();
        sb.Append("COPY ").Append(schema.TargetTable).Append(" (");

        if (schema.IncludesCollectionId)
        {
            sb.Append(schema.PrefixIdColumnName).Append(", ");
        }

        sb.Append(schema.PrefixTimeColumnName).Append(", server_id, server_name");

        foreach (var column in schema.PayloadColumns)
        {
            sb.Append(", ").Append(column.Name);
        }

        sb.Append(") FROM STDIN (FORMAT BINARY)");
        return sb.ToString();
    }

    /// <summary>Naive-UTC storage: strip the Kind so Npgsql accepts the value for `timestamp`.</summary>
    private static DateTime Naive(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Unspecified);

    /// <summary>
    /// Postgres `text` cannot hold NUL (0x00), which SQL Server NVARCHAR allows — one NUL-laden
    /// query text from dm_exec_sql_text fails the whole COPY batch with 22021 "invalid byte
    /// sequence for encoding UTF8: 0x00" (#1614). Every collector string funnels through
    /// <see cref="Value(string?)"/>, so stripping here covers all of them. String.Replace returns
    /// the original instance when nothing matches, so clean strings (the vast majority) don't allocate.
    /// </summary>
    public static string StripEmbeddedNuls(string value) => value.Replace("\0", string.Empty);

    public ICollectorRowWriter Value(string? value)
    {
        if (value is null) { Target.WriteNull(); } else { Target.Write(StripEmbeddedNuls(value), NpgsqlDbType.Text); }
        return this;
    }

    public ICollectorRowWriter Value(long value)
    {
        Target.Write(value, NpgsqlDbType.Bigint);
        return this;
    }

    public ICollectorRowWriter Value(long? value)
    {
        if (value is null) { Target.WriteNull(); } else { Target.Write(value.Value, NpgsqlDbType.Bigint); }
        return this;
    }

    public ICollectorRowWriter Value(int value)
    {
        Target.Write(value, NpgsqlDbType.Integer);
        return this;
    }

    public ICollectorRowWriter Value(int? value)
    {
        if (value is null) { Target.WriteNull(); } else { Target.Write(value.Value, NpgsqlDbType.Integer); }
        return this;
    }

    public ICollectorRowWriter Value(short value)
    {
        Target.Write(value, NpgsqlDbType.Smallint);
        return this;
    }

    public ICollectorRowWriter Value(short? value)
    {
        if (value is null) { Target.WriteNull(); } else { Target.Write(value.Value, NpgsqlDbType.Smallint); }
        return this;
    }

    public ICollectorRowWriter Value(double value)
    {
        Target.Write(value, NpgsqlDbType.Double);
        return this;
    }

    public ICollectorRowWriter Value(double? value)
    {
        if (value is null) { Target.WriteNull(); } else { Target.Write(value.Value, NpgsqlDbType.Double); }
        return this;
    }

    public ICollectorRowWriter Value(decimal value)
    {
        Target.Write(value, NpgsqlDbType.Numeric);
        return this;
    }

    public ICollectorRowWriter Value(decimal? value)
    {
        if (value is null) { Target.WriteNull(); } else { Target.Write(value.Value, NpgsqlDbType.Numeric); }
        return this;
    }

    public ICollectorRowWriter Value(bool value)
    {
        Target.Write(value, NpgsqlDbType.Boolean);
        return this;
    }

    public ICollectorRowWriter Value(bool? value)
    {
        if (value is null) { Target.WriteNull(); } else { Target.Write(value.Value, NpgsqlDbType.Boolean); }
        return this;
    }

    public ICollectorRowWriter Value(DateTime value)
    {
        Target.Write(Naive(value), NpgsqlDbType.Timestamp);
        return this;
    }

    public ICollectorRowWriter Value(DateTime? value)
    {
        if (value is null) { Target.WriteNull(); } else { Target.Write(Naive(value.Value), NpgsqlDbType.Timestamp); }
        return this;
    }

    public ICollectorRowWriter NullValue()
    {
        Target.WriteNull();
        return this;
    }
}
