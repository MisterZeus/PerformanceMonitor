using Installer.Core;
using Installer.Core.Models;

namespace Installer.Tests;

/// <summary>
/// GenerateSummaryReport builds a filename out of the user-typed server name, and both front ends call it.
/// It used to strip only the backslash, so a server name like "x/../../foo" turned Path.Combine into a
/// write OUTSIDE the report directory -- forward slash is a separator and ".." is parent. These pin that
/// the report always lands inside the directory it was given, whatever the server name contains.
/// </summary>
public class SummaryReportPathTests
{
    private static InstallationResult AResult() => new()
    {
        Success = true,
        FilesSucceeded = 1,
        StartTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        EndTime = new DateTime(2026, 1, 1, 0, 0, 1, DateTimeKind.Utc)
    };

    [Theory]
    [InlineData(@"x/../../../../Windows/Temp/pwn")]   // forward-slash traversal
    [InlineData(@"..\..\..\..\Users\Public\pwn")]     // backslash traversal
    [InlineData(@"SQL01:evil")]                        // NTFS alternate data stream
    [InlineData(@"a/b/c")]                             // plain separators
    [InlineData(@"SQL01\INSTANCE")]                    // ordinary, legitimate instance name
    public void Report_AlwaysLandsInsideTheGivenDirectory(string serverName)
    {
        string dir = Path.Combine(Path.GetTempPath(), "pm_report_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string written = InstallationService.GenerateSummaryReport(
                serverName, "v", "Edition", "3.1.0", AResult(), dir);

            string fullDir = Path.GetFullPath(dir);
            string fullWritten = Path.GetFullPath(written);

            Assert.StartsWith(
                fullDir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                fullWritten,
                StringComparison.OrdinalIgnoreCase);

            // And it is directly in the directory -- one segment, no traversal survived.
            Assert.Equal(fullDir.TrimEnd(Path.DirectorySeparatorChar),
                Path.GetDirectoryName(fullWritten)!.TrimEnd(Path.DirectorySeparatorChar));

            Assert.True(File.Exists(written));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
