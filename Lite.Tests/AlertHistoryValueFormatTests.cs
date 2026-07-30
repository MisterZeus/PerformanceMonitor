using PerformanceMonitorLite.Services;
using Xunit;

namespace PerformanceMonitorLite.Tests;

/// <summary>
/// Locks in #1134: Lite's Alert History Value/Threshold columns bind to
/// <see cref="AlertHistoryRow.CurrentValueDisplay"/> / <see cref="AlertHistoryRow.ThresholdValueDisplay"/>,
/// which run the stored DuckDB <c>current_value</c>/<c>threshold_value</c> doubles through
/// <c>FormatValue</c>. That method formerly special-cased only CPU/TempDB and fell through to ":G"
/// for everything else, leaking the raw stored double — the reported Volume Free Space
/// "0.9746057751382348". It is now keyed on the exact metric_name strings the Lite alert engine
/// emits (MainWindow.AlertEngine.cs), with a ":F2" fallback that can never render a raw
/// full-precision float. Dashboard is structurally immune (its Value is a pre-formatted string built
/// at the alert site), so this is Lite-only and these tests live in Lite.Tests.
/// </summary>
public class AlertHistoryValueFormatTests
{
    private static AlertHistoryRow Row(string metricName, double current) =>
        new() { MetricName = metricName, CurrentValue = current };

    [Fact]
    public void VolumeFreeSpace_RawFloat_RendersAsOneDecimalPercent()
    {
        // The exact value from the issue report must not leak as a raw float.
        Assert.Equal("1.0%", Row("Volume Free Space", 0.9746057751382348).CurrentValueDisplay);
    }

    [Theory]
    [InlineData("High CPU", 45.0, "45.0%")]
    [InlineData("tempdb Space", 87.3, "87.3%")]
    [InlineData("Volume Free Space", 9.74, "9.7%")]
    [InlineData("Long-Running Job", 247.8, "247.8%")] // job's "% of average"
    public void PercentMetrics_RenderOneDecimalPercent(string metric, double value, string expected)
    {
        Assert.Equal(expected, Row(metric, value).CurrentValueDisplay);
    }

    [Fact]
    public void LegacyTempDbSpaceName_StillRendersAsPercent()
    {
        /* The tempdb token was lowercased across both apps' UI (c0109f34), which changed this metric's
           metric_name KEY from "TempDB Space" to "tempdb Space"; that commit accepted that stored
           alert-history rows keep the old name. Matching here is ordinal, so those rows fell through to
           the :F2 default and showed a percentage with no unit. Both spellings must format identically. */
        Assert.Equal("87.3%", Row("TempDB Space", 87.3).CurrentValueDisplay);
        Assert.Equal(Row("tempdb Space", 87.3).CurrentValueDisplay, Row("TempDB Space", 87.3).CurrentValueDisplay);
    }

    [Fact]
    public void PoisonWait_RendersWholeMilliseconds()
    {
        Assert.Equal("1235 ms", Row("Poison Wait", 1234.56).CurrentValueDisplay);
    }

    [Fact]
    public void LongRunningQuery_RendersWholeMinutes()
    {
        Assert.Equal("12 m", Row("Long-Running Query", 12.0).CurrentValueDisplay);
    }

    [Theory]
    [InlineData("Blocking Detected", 3.0, "3")]
    [InlineData("Deadlocks Detected", 2.0, "2")]
    [InlineData("Failed Agent Job", 1.0, "1")]
    public void CountMetrics_RenderWholeNumbers(string metric, double value, string expected)
    {
        Assert.Equal(expected, Row(metric, value).CurrentValueDisplay);
    }

    [Fact]
    public void ThresholdColumn_UsesSameUnitAwareFormat()
    {
        // The Threshold column binds to ThresholdValueDisplay through the same formatter; the issue
        // showed a bare "10" for the Volume Free Space threshold, which now carries its unit.
        var row = new AlertHistoryRow { MetricName = "Volume Free Space", ThresholdValue = 10.0 };
        Assert.Equal("10.0%", row.ThresholdValueDisplay);
    }

    [Fact]
    public void UnmappedMetric_FallsBackToTwoDecimals_NeverRawFloat()
    {
        // Analysis findings log metric names like "Analysis: CPU [a1b2c3d4]" carrying a raw severity
        // double; the old ":G" fallback would leak its full precision. ":F2" must bound it.
        var display = Row("Analysis: CPU [a1b2c3d4]", 0.9746057751382348).CurrentValueDisplay;
        Assert.Equal("0.97", display);
        Assert.DoesNotContain("0.9746", display);
    }
}
