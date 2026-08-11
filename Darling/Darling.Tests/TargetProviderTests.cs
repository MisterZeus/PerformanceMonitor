/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using Microsoft.Data.SqlClient;
using Npgsql;
using PerformanceMonitor.Collectors;
using PerformanceMonitor.Darling.Service.Targets;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// Pins the engine-execution seam: the right driver types come out of each provider, the collector
/// parameter mapping is total for both engines, and a driver failure is named the same way for both.
/// </summary>
public class TargetProviderTests
{
    private static CollectorQuery Query(params CollectorParameter[] parameters)
        => new("SELECT 1", parameters);

    [Fact]
    public void ResolvesAProviderForEveryDeclaredEngine()
    {
        foreach (CollectorTargetEngine engine in Enum.GetValues<CollectorTargetEngine>())
        {
            var provider = TargetProviders.For(engine);
            Assert.Equal(engine, provider.Engine);
        }
    }

    [Fact]
    public void ProducesTheDriverTypesEachEngineNeeds()
    {
        Assert.IsType<SqlConnection>(SqlServerTargetProvider.Instance.CreateConnection("Server=nowhere"));
        Assert.IsType<NpgsqlConnection>(PostgresTargetProvider.Instance.CreateConnection("Host=nowhere"));
    }

    /// <summary>
    /// Every parameter type a definition can declare must map on BOTH engines. An unmapped type
    /// throwing at runtime, inside a sweep, is the failure this prevents.
    /// </summary>
    [Fact]
    public void MapsEveryCollectorParameterTypeOnBothEngines()
    {
        foreach (CollectorParameterType type in Enum.GetValues<CollectorParameterType>())
        {
            var query = Query(new CollectorParameter("@p", Value(type), type));

            using var sqlConnection = new SqlConnection("Server=nowhere");
            using var sqlCommand = SqlServerTargetProvider.Instance.CreateCommand(query, sqlConnection, 60);
            Assert.Single(sqlCommand.Parameters);

            using var pgConnection = new NpgsqlConnection("Host=nowhere");
            using var pgCommand = PostgresTargetProvider.Instance.CreateCommand(query, pgConnection, 60);
            Assert.Single(pgCommand.Parameters);
        }

        static object Value(CollectorParameterType type) => type switch
        {
            CollectorParameterType.DateTime2 => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified),
            CollectorParameterType.Int32 => 1,
            CollectorParameterType.BigInt => 1L,
            _ => "x",
        };
    }

    /// <summary>A null parameter value becomes DBNull, not a null reference, on both engines.</summary>
    [Fact]
    public void MapsNullParameterValuesToDbNull()
    {
        var query = Query(new CollectorParameter("@p", null, CollectorParameterType.NVarChar128));

        using var sqlConnection = new SqlConnection("Server=nowhere");
        using var sqlCommand = SqlServerTargetProvider.Instance.CreateCommand(query, sqlConnection, 60);
        Assert.Equal(DBNull.Value, sqlCommand.Parameters[0].Value);

        using var pgConnection = new NpgsqlConnection("Host=nowhere");
        using var pgCommand = PostgresTargetProvider.Instance.CreateCommand(query, pgConnection, 60);
        Assert.Equal(DBNull.Value, pgCommand.Parameters[0].Value);
    }

    [Fact]
    public void RejectsAConnectionFromTheWrongEngine()
    {
        using var pgConnection = new NpgsqlConnection("Host=nowhere");
        Assert.Throws<ArgumentException>(() =>
            SqlServerTargetProvider.Instance.CreateCommand(Query(), pgConnection, 60));

        using var sqlConnection = new SqlConnection("Server=nowhere");
        Assert.Throws<ArgumentException>(() =>
            PostgresTargetProvider.Instance.CreateCommand(Query(), sqlConnection, 60));
    }

    [Fact]
    public void AppliesTheCommandTimeoutOnBothEngines()
    {
        using var sqlConnection = new SqlConnection("Server=nowhere");
        using var sqlCommand = SqlServerTargetProvider.Instance.CreateCommand(Query(), sqlConnection, 300);
        Assert.Equal(300, sqlCommand.CommandTimeout);

        using var pgConnection = new NpgsqlConnection("Host=nowhere");
        using var pgCommand = PostgresTargetProvider.Instance.CreateCommand(Query(), pgConnection, 300);
        Assert.Equal(300, pgCommand.CommandTimeout);
    }

    /// <summary>
    /// The Postgres SQLSTATEs here are the ones actually observed while probing our Aurora fleet:
    /// 42501 from a function needing rds_replication, 42P01 from pg_stat_statements in a database
    /// where the view was never created, 0A000 from pg_stat_wal (Aurora blocks it), and a 55-class
    /// error from a feature that is switched off rather than empty.
    /// </summary>
    [Theory]
    [InlineData("42501", CollectorTargetFault.Permissions)]
    [InlineData("42P01", CollectorTargetFault.ObjectMissing)]
    [InlineData("42883", CollectorTargetFault.ObjectMissing)]
    [InlineData("0A000", CollectorTargetFault.FeatureDisabled)]
    [InlineData("55000", CollectorTargetFault.FeatureDisabled)]
    [InlineData("57014", CollectorTargetFault.CommandTimeout)]
    [InlineData("08006", CollectorTargetFault.ConnectionFatal)]
    [InlineData("08000", CollectorTargetFault.ConnectionFatal)]
    [InlineData("57P01", CollectorTargetFault.ConnectionFatal)]
    [InlineData("XX000", CollectorTargetFault.Unclassified)]
    public void ClassifiesPostgresSqlStates(string sqlState, CollectorTargetFault expected)
    {
        var exception = new PostgresException("boom", "ERROR", "ERROR", sqlState);
        Assert.Equal(expected, PostgresTargetProvider.Instance.Classify(exception, yieldsOnLockTimeout: false));
    }

    /// <summary>
    /// A lock timeout is a yield only for a collector that deliberately set a short lock timeout.
    /// Same rule, both engines — it is a property of the collector, not of the database.
    /// </summary>
    [Fact]
    public void TreatsALockTimeoutAsAYieldOnlyForCollectorsThatOptIn()
    {
        var pgLockTimeout = new PostgresException("boom", "ERROR", "ERROR", "55P03");

        Assert.Equal(
            CollectorTargetFault.LockTimeoutYield,
            PostgresTargetProvider.Instance.Classify(pgLockTimeout, yieldsOnLockTimeout: true));
        Assert.Equal(
            CollectorTargetFault.Unclassified,
            PostgresTargetProvider.Instance.Classify(pgLockTimeout, yieldsOnLockTimeout: false));
    }

    [Fact]
    public void ClassifiesUnrecognizedExceptionsAsUnclassifiedSoTheyStayLoud()
    {
        var boom = new InvalidOperationException("something else entirely");

        Assert.Equal(CollectorTargetFault.Unclassified, SqlServerTargetProvider.Instance.Classify(boom, false));
        Assert.Equal(CollectorTargetFault.Unclassified, PostgresTargetProvider.Instance.Classify(boom, false));
    }

    [Fact]
    public void ClassifiesATimeoutExceptionAsACommandTimeoutOnPostgres()
    {
        Assert.Equal(
            CollectorTargetFault.CommandTimeout,
            PostgresTargetProvider.Instance.Classify(new TimeoutException(), false));
    }
}
