#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace demo.Performance;

public static class BenchmarkStatistics
{
    /// <summary>
    /// Uses linear interpolation between adjacent ordered samples. Empty input
    /// returns null, allowing the result to remain valid JSON without NaN values.
    /// </summary>
    public static double? Percentile(IEnumerable<double> values, double percentile)
    {
        if (values == null)
        {
            throw new ArgumentNullException(nameof(values));
        }
        if (percentile < 0 || percentile > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(percentile), "Percentile must be between 0 and 100.");
        }

        var sorted = ValidateAndSort(values);
        if (sorted.Length == 0)
        {
            return null;
        }
        return PercentileFromSorted(sorted, percentile);
    }

    public static BenchmarkMetricSummary Summarize(IEnumerable<double> values)
    {
        if (values == null)
        {
            throw new ArgumentNullException(nameof(values));
        }

        var sorted = ValidateAndSort(values);
        if (sorted.Length == 0)
        {
            return new BenchmarkMetricSummary();
        }

        return new BenchmarkMetricSummary
        {
            Count = sorted.Length,
            Minimum = sorted[0],
            Maximum = sorted[^1],
            Mean = sorted.Average(),
            P50 = PercentileFromSorted(sorted, 50),
            P90 = PercentileFromSorted(sorted, 90),
            P95 = PercentileFromSorted(sorted, 95),
        };
    }

    private static double PercentileFromSorted(double[] sorted, double percentile)
    {
        double rank = percentile / 100d * (sorted.Length - 1);
        int lower = (int)Math.Floor(rank);
        int upper = (int)Math.Ceiling(rank);
        if (lower == upper)
        {
            return sorted[lower];
        }

        double fraction = rank - lower;
        return sorted[lower] + (sorted[upper] - sorted[lower]) * fraction;
    }

    private static double[] ValidateAndSort(IEnumerable<double> values)
    {
        var sorted = values.ToArray();
        if (Array.Exists(sorted, value => !double.IsFinite(value)))
        {
            throw new ArgumentException("Benchmark samples must be finite numbers.", nameof(values));
        }
        Array.Sort(sorted);
        return sorted;
    }
}
