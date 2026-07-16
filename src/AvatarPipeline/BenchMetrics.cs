//-----------------------------------------------------------------------------
// Filename: BenchMetrics.cs
//
// Description: Lightweight in-process stage-timing collector for the avatar
// pipeline. Stages call Record(...) with a name and elapsed milliseconds; the
// bench endpoints in Max expose the ring buffer plus percentile aggregates so
// an external bench client (src/MaxBench) can measure LLM reply latency, TTS
// synthesis time and lip-sync lag without a profiler attached. Recording is a
// ConcurrentQueue append - cheap enough to leave enabled in production.
//
// Author(s):
// sipsorcery-claude (aaron+claude@sipsorcery.com)
//
// License:
// BSD 3-Clause "New" or "Revised" License, see included LICENSE.md file.
//-----------------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace demo;

public static class BenchMetrics
{
    /// <summary>One recorded stage timing.</summary>
    public sealed record Entry(string Name, double Ms, string Detail, DateTimeOffset At);

    private const int MaxEntries = 2000;

    private static readonly ConcurrentQueue<Entry> _entries = new();

    /// <summary>Records one stage timing. Never throws; safe on hot paths.</summary>
    public static void Record(string name, double ms, string detail = null)
    {
        _entries.Enqueue(new Entry(name, ms, detail, DateTimeOffset.UtcNow));
        // Approximate cap: concurrent enqueuers can overshoot by a few entries,
        // which is harmless for a diagnostics buffer.
        while (_entries.Count > MaxEntries && _entries.TryDequeue(out _)) { }
    }

    /// <summary>All buffered entries, oldest first.</summary>
    public static IReadOnlyList<Entry> Snapshot() => _entries.ToArray();

    /// <summary>Clears the buffer (bench runs call this to isolate a measurement pass).</summary>
    public static void Reset()
    {
        while (_entries.TryDequeue(out _)) { }
    }

    /// <summary>Per-stage aggregates over the current buffer.</summary>
    public static Dictionary<string, object> Aggregates()
    {
        return Snapshot()
            .GroupBy(e => e.Name)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var sorted = g.Select(e => e.Ms).OrderBy(v => v).ToArray();
                    return (object)new
                    {
                        count = sorted.Length,
                        mean = Math.Round(sorted.Average(), 1),
                        min = sorted[0],
                        p50 = Percentile(sorted, 0.50),
                        p95 = Percentile(sorted, 0.95),
                        max = sorted[^1],
                    };
                });
    }

    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 1) { return sorted[0]; }
        var rank = p * (sorted.Length - 1);
        int lo = (int)rank;
        double frac = rank - lo;
        return Math.Round(sorted[lo] + (lo + 1 < sorted.Length ? frac * (sorted[lo + 1] - sorted[lo]) : 0), 1);
    }
}
