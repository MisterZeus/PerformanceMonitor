/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.IO;
using System.Text.RegularExpressions;
using PerformanceMonitor.Common;
using Xunit;

namespace PerformanceMonitorLite.Tests;

/// <summary>
/// #2425. One trailing comma in a hand-edited settings.json used to reset all eighty-eight Lite settings
/// at once, say nothing about it anywhere, and then leave the file exposed to the next whole-document
/// rewrite. Two properties are pinned here, and the second is the one that turns an annoyance into data
/// loss.
///
/// <para><b>Absent is not unreadable.</b> The old loaders had a single bare <c>catch</c>, so a first run
/// with no file and a corrupt file took the same silent path to defaults. That is why the silence looked
/// reasonable in the code and was wrong in practice — half the traffic through it really was fine. The
/// split has to hold in BOTH directions: a missing file must stay completely silent, because a first-run
/// warning is pure noise, and a present-but-broken file must never be.</para>
///
/// <para><b>Nothing overwrites what it could not read.</b> Every Save in Lite rewrites the whole document,
/// including saves nobody thinks of as saves, so a writer that starts from a fresh object after a failed
/// parse destroys the only record of the user's real configuration. The copy has to exist BEFORE the write,
/// and the original has to survive making it.</para>
/// </summary>
public sealed class SettingsFileGuardTests
{
    /// <summary>A settings.json a user would recognize: real keys, one syntax error.</summary>
    private const string TrailingComma = @"{
  ""alerts_enabled"": true,
  ""alert_cpu_threshold"": 91,
}";

    private static string NewTempDir(string tag)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"pmlite_{tag}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string WriteSettings(string dir, string content)
    {
        var path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>
    /// The legitimate first run. No file, no problem, nothing to say — and specifically no Problem string,
    /// because anything non-null there becomes a log line and a dialog for a user who has done nothing
    /// wrong.
    /// </summary>
    [Fact]
    public void Read_IsSilentlyAbsent_WhenThereIsNoFile()
    {
        var dir = NewTempDir("absent");
        try
        {
            var read = SettingsFileGuard.Read(Path.Combine(dir, "settings.json"));

            Assert.Equal(SettingsFileState.Absent, read.State);
            Assert.Null(read.Problem);
            Assert.Null(read.Root);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Read_IsReadable_ForAnOrdinarySettingsFile()
    {
        var dir = NewTempDir("ok");
        try
        {
            var path = WriteSettings(dir, @"{""alerts_enabled"":true,""alert_cpu_threshold"":91}");

            var read = SettingsFileGuard.Read(path);

            Assert.Equal(SettingsFileState.Readable, read.State);
            Assert.Null(read.Problem);
            Assert.NotNull(read.Root);
            Assert.True(read.Root!["alerts_enabled"]!.GetValue<bool>());
            Assert.NotNull(read.Text);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// The headline case, and the reason the diagnostic carries a position rather than the word "failed":
    /// "settings.json is broken" sends someone looking through a file they already believe is correct,
    /// while "line 4" is a minute's work. The System.Text.Json " Path: $ | LineNumber: ..." tail is cut
    /// because the same facts are already in the sentence, in the form a person reads them.
    /// </summary>
    [Fact]
    public void Read_ReportsTheLineAndPosition_ForATrailingComma()
    {
        var dir = NewTempDir("comma");
        try
        {
            var path = WriteSettings(dir, TrailingComma);

            var read = SettingsFileGuard.Read(path);

            Assert.Equal(SettingsFileState.Unreadable, read.State);
            Assert.NotNull(read.Problem);
            Assert.Contains("line 4", read.Problem!, StringComparison.Ordinal);
            Assert.Contains("position", read.Problem!, StringComparison.Ordinal);
            Assert.DoesNotContain(" Path: ", read.Problem!, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// A file holding the JSON literal null parses fine and is still unusable. It gets its own case because
    /// it is the shape that slipped past the writers' old <c>JsonNode.Parse(json) ?? new JsonObject()</c>
    /// read: no exception, no warning, and the very next Save replaced the document with a fresh one
    /// holding a single key.
    /// </summary>
    [Fact]
    public void Read_IsUnreadable_ForARootThatIsNotAnObject()
    {
        var dir = NewTempDir("notobject");
        try
        {
            Assert.Equal(SettingsFileState.Unreadable, SettingsFileGuard.Read(WriteSettings(dir, "null")).State);

            var array = SettingsFileGuard.Read(WriteSettings(dir, "[1,2,3]"));
            Assert.Equal(SettingsFileState.Unreadable, array.State);
            Assert.Contains("array", array.Problem!, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// An empty file has nothing to preserve but plenty to explain — settings that were there yesterday are
    /// gone today, and a write interrupted by a full disk or a crash is the likeliest reason. Reported, not
    /// quietly folded into "absent".
    /// </summary>
    [Fact]
    public void Read_IsUnreadable_ForAnEmptyFile()
    {
        var dir = NewTempDir("empty");
        try
        {
            var read = SettingsFileGuard.Read(WriteSettings(dir, "   \n"));

            Assert.Equal(SettingsFileState.Unreadable, read.State);
            Assert.NotNull(read.Problem);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// The path that has always worked keeps working: a readable file is MERGED into, so keys the writer
    /// never mentions — hand-edited ones with no UI, most of all — survive the save.
    /// </summary>
    [Fact]
    public void RootForWrite_MergesIntoTheExistingDocument_WhenReadable()
    {
        var dir = NewTempDir("merge");
        try
        {
            var path = WriteSettings(dir, @"{""check_for_updates_on_startup"":false,""alert_cpu_threshold"":91}");

            var forWrite = SettingsFileGuard.RootForWrite(path, DateTime.Now);

            Assert.Null(forWrite.Problem);
            Assert.Null(forWrite.QuarantinedTo);
            Assert.False(forWrite.Root["check_for_updates_on_startup"]!.GetValue<bool>());
            Assert.Equal(91, forWrite.Root["alert_cpu_threshold"]!.GetValue<int>());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// The first run again, from the writer's side: no file means a fresh document and still no diagnostic.
    /// A quarantine copy here would be litter, and a warning would be a lie.
    /// </summary>
    [Fact]
    public void RootForWrite_StartsFreshAndSilent_WhenTheFileIsAbsent()
    {
        var dir = NewTempDir("firstwrite");
        try
        {
            var forWrite = SettingsFileGuard.RootForWrite(Path.Combine(dir, "settings.json"), DateTime.Now);

            Assert.Null(forWrite.Problem);
            Assert.Null(forWrite.QuarantinedTo);
            Assert.Empty(forWrite.Root);
            Assert.Empty(Directory.GetFiles(dir));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// The data-loss case. An unreadable file is copied aside BEFORE the caller is handed a document to
    /// write, the copy holds the original bytes verbatim, and the original is still where it was — a move
    /// would leave a hole if the write that follows also failed.
    /// </summary>
    [Fact]
    public void RootForWrite_CopiesTheUnreadableFileAside_BeforeHandingBackAFreshDocument()
    {
        var dir = NewTempDir("quarantine");
        try
        {
            var path = WriteSettings(dir, TrailingComma);

            var forWrite = SettingsFileGuard.RootForWrite(path, new DateTime(2026, 8, 21, 14, 5, 2, DateTimeKind.Local));

            Assert.NotNull(forWrite.Problem);
            Assert.NotNull(forWrite.QuarantinedTo);
            Assert.Equal(path + ".unreadable-20260821-140502", forWrite.QuarantinedTo);
            Assert.Equal(TrailingComma, File.ReadAllText(forWrite.QuarantinedTo!));

            /* Copied, not moved: the caller's write can still fail. */
            Assert.Equal(TrailingComma, File.ReadAllText(path));

            /* And what the caller writes must not carry anything from the file nobody could read. */
            Assert.Empty(forWrite.Root);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// Two unreadable files quarantined inside the same second must not collide, because the collision
    /// would destroy exactly the bytes the first copy was made to keep. Same timestamp, twice, deliberately.
    /// </summary>
    [Fact]
    public void Quarantine_DoesNotOverwriteAnEarlierCopyFromTheSameSecond()
    {
        var dir = NewTempDir("collide");
        try
        {
            var path = WriteSettings(dir, TrailingComma);
            var stamp = new DateTime(2026, 8, 21, 14, 5, 2, DateTimeKind.Local);

            var first = SettingsFileGuard.Quarantine(path, stamp);
            File.WriteAllText(path, "{ still not json");
            var second = SettingsFileGuard.Quarantine(path, stamp);

            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.NotEqual(first, second);
            Assert.Equal(TrailingComma, File.ReadAllText(first!));
            Assert.Equal("{ still not json", File.ReadAllText(second!));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

/// <summary>
/// The category guard behind #2425, rather than the instance. The defect was never "WriteSetting is
/// wrong" — it was that five separate methods each rolled their own read of settings.json in front of a
/// whole-document rewrite, so the safety of a Save depended on which method you happened to be in. A sixth
/// written the same way tomorrow would be just as silent, and nothing but this would notice.
///
/// <para>Source-parsing because the invariant is a wiring one: each write site must take the read that
/// preserves what it cannot parse. A behavioral test can prove the guard works and still not notice a
/// caller that never asks it.</para>
/// </summary>
public sealed class SettingsWriterQuarantineWiringTests
{
    public static TheoryData<string> WritingFiles() => new()
    {
        Path.Combine("Lite", "App.xaml.cs"),
        Path.Combine("Lite", "Windows", "SettingsWindow.xaml.cs")
    };

    [Theory]
    [MemberData(nameof(WritingFiles))]
    public void EverySettingsJsonRewrite_TakesTheQuarantiningRead(string relativePath)
    {
        var source = File.ReadAllText(FindRepoFile(relativePath));

        var rewrites = Regex.Matches(source, @"File\.WriteAllText\(settingsPath").Count;
        var guardedReads = Regex.Matches(source, @"[=(,]\s*(?:App\.)?SettingsRootForWrite\(\)").Count;

        Assert.True(rewrites > 0,
            $"{relativePath}: no settings.json rewrite found — this guard's anchor moved and it is testing nothing.");
        Assert.True(rewrites == guardedReads,
            $"{relativePath}: {rewrites} whole-document rewrite(s) of settings.json but {guardedReads} call(s) to " +
            "App.SettingsRootForWrite(). A rewrite that reads the file itself will replace an unparseable " +
            "settings.json without copying it aside first, which is #2425 all over again.");
    }

    /// <summary>
    /// The exact shape that made a non-object root a silent total overwrite: Parse returns null for the JSON
    /// literal null, the null-coalesce reads that as "no file", and the save replaces the document. Banned
    /// outright so it cannot be reintroduced by copy-paste from a sibling writer.
    /// </summary>
    [Theory]
    [MemberData(nameof(WritingFiles))]
    public void NoWriter_FallsBackToAFreshDocumentOnAFailedParse(string relativePath)
    {
        var source = File.ReadAllText(FindRepoFile(relativePath));

        Assert.DoesNotMatch(new Regex(@"JsonNode\.Parse\([^;]*\)\s*\?\?\s*new JsonObject\(\)"), source);
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
