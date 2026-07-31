/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using Xunit;

namespace Darling.Tests;

/// <summary>
/// The definition behind the pre-existing <c>[Collection("live-postgres")]</c> attribute, which until now had
/// none — so the collection serialized its sixty-odd members against each other but never gave them a shared
/// STARTING STATE. <see cref="LivePostgresStoreFixture"/> supplies that: xUnit builds a collection fixture and
/// awaits its initialization before the first class in the collection runs, so every live test now opens onto
/// a migrated store with the TimescaleDB extension present, whatever order the runner picks (#1862).
///
/// <para>Same shape as <see cref="ViewerTimeStaticsCollection"/>, and for the same class of bug: a
/// cross-class dependency that lands on a DIFFERENT victim each run and so reads as an unrelated flake.
/// The difference is which lever each one pulls — that collection needs
/// <c>DisableParallelization</c> because its members mutate process-wide statics that non-members read;
/// this one deliberately does NOT, because its members already serialize against each other by carrying the
/// attribute and the classes outside it mint their own databases (the <c>#1776 own-store</c> exemption
/// <see cref="LivePostgresCollectionHygieneTests"/> enforces). Serializing the whole assembly behind the live
/// store would cost a large multiple of the suite's runtime and buy nothing.</para>
/// </summary>
[CollectionDefinition("live-postgres")]
public sealed class LivePostgresCollection : ICollectionFixture<LivePostgresStoreFixture>
{
}
