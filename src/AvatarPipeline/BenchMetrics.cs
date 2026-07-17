using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace demo;

/// <summary>Lightweight process-wide stage timings exposed by the opt-in bench API.</summary>
public static class BenchMetrics
{
    public sealed record Entry(string Name, double Ms, string Detail, DateTimeOffset At);

    private const int MaxEntries = 2000;
    private static readonly ConcurrentQueue<Entry> _entries = new();
    private static Action<string> _benchmarkEventSink;

    public static void SetBenchmarkEventSink(Action<string> sink) =>
        System.Threading.Volatile.Write(ref _benchmarkEventSink, sink);

    public static void ClearBenchmarkEventSink() =>
        System.Threading.Volatile.Write(ref _benchmarkEventSink, null);

    public static void Record(string name, double ms, string detail = null)
    {
        _entries.Enqueue(new Entry(name, ms, detail, DateTimeOffset.UtcNow));
        var eventName = name switch
        {
            "llm_ttft" => "llm_first_token",
            "tts_first_chunk" => "audio_started",
            "lipsync_first_mouth" => "first_mouth_frame",
            _ => null,
        };
        if (eventName != null)
        {
            System.Threading.Volatile.Read(ref _benchmarkEventSink)?.Invoke(eventName);
        }
        while (_entries.Count > MaxEntries && _entries.TryDequeue(out _)) { }
    }

    public static IReadOnlyList<Entry> Snapshot() => _entries.ToArray();

    public static void Reset()
    {
        while (_entries.TryDequeue(out _)) { }
    }

    public static Dictionary<string, object> Aggregates() =>
        Snapshot()
            .GroupBy(entry => entry.Name)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var sorted = group.Select(entry => entry.Ms).OrderBy(value => value).ToArray();
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

    private static double Percentile(double[] sorted, double percentile)
    {
        if (sorted.Length == 1) return sorted[0];
        var rank = percentile * (sorted.Length - 1);
        var lower = (int)rank;
        var fraction = rank - lower;
        return Math.Round(
            sorted[lower] + (lower + 1 < sorted.Length ? fraction * (sorted[lower + 1] - sorted[lower]) : 0),
            1);
    }
}
