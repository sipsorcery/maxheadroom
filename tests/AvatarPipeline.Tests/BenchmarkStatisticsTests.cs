using demo.Performance;

namespace AvatarPipeline.Tests;

public sealed class BenchmarkStatisticsTests
{
    [Fact]
    public void Summarize_EmptySequence_ProducesJsonSafeEmptySummary()
    {
        var summary = BenchmarkStatistics.Summarize(Array.Empty<double>());

        Assert.Equal(0, summary.Count);
        Assert.Null(summary.Minimum);
        Assert.Null(summary.Maximum);
        Assert.Null(summary.Mean);
        Assert.Null(summary.P50);
        Assert.Null(summary.P90);
        Assert.Null(summary.P95);
    }

    [Fact]
    public void Summarize_SingleValue_UsesValueForEveryStatistic()
    {
        var summary = BenchmarkStatistics.Summarize(new[] { 42d });

        Assert.Equal(1, summary.Count);
        Assert.Equal(42d, summary.Minimum);
        Assert.Equal(42d, summary.Maximum);
        Assert.Equal(42d, summary.Mean);
        Assert.Equal(42d, summary.P50);
        Assert.Equal(42d, summary.P90);
        Assert.Equal(42d, summary.P95);
    }

    [Fact]
    public void Summarize_OddSequence_UsesLinearInterpolation()
    {
        var summary = BenchmarkStatistics.Summarize(new[] { 5d, 1d, 4d, 2d, 3d });

        Assert.Equal(5, summary.Count);
        Assert.Equal(1d, summary.Minimum);
        Assert.Equal(5d, summary.Maximum);
        Assert.Equal(3d, summary.Mean);
        Assert.Equal(3d, summary.P50);
        Assert.NotNull(summary.P90);
        Assert.NotNull(summary.P95);
        Assert.Equal(4.6d, summary.P90.Value, 10);
        Assert.Equal(4.8d, summary.P95.Value, 10);
    }

    [Fact]
    public void Summarize_EvenSequence_InterpolatesMedianAndTail()
    {
        var summary = BenchmarkStatistics.Summarize(new[] { 4d, 2d, 1d, 3d });

        Assert.Equal(2.5d, summary.P50);
        Assert.NotNull(summary.P90);
        Assert.NotNull(summary.P95);
        Assert.Equal(3.7d, summary.P90.Value, 10);
        Assert.Equal(3.85d, summary.P95.Value, 10);
    }

    [Fact]
    public void Percentile_RejectsInvalidPercentile()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BenchmarkStatistics.Percentile(new[] { 1d }, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => BenchmarkStatistics.Percentile(new[] { 1d }, 101));
    }

    [Fact]
    public void Summarize_RejectsNonFiniteSamples()
    {
        Assert.Throws<ArgumentException>(() => BenchmarkStatistics.Summarize(new[] { 1d, double.NaN }));
        Assert.Throws<ArgumentException>(() => BenchmarkStatistics.Summarize(new[] { 1d, double.PositiveInfinity }));
    }
}
