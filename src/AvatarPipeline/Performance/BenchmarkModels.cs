#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace demo.Performance;

public static class BenchmarkSchema
{
    public const int CurrentVersion = 1;
}

public sealed class BenchmarkRunResult
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = BenchmarkSchema.CurrentVersion;

    [JsonPropertyName("runId")]
    public string RunId { get; init; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("startedAtUtc")]
    public DateTimeOffset StartedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("source")]
    public BenchmarkSourceInfo Source { get; init; } = new();

    [JsonPropertyName("environment")]
    public BenchmarkEnvironmentInfo Environment { get; init; } = BenchmarkEnvironmentInfo.Capture();

    [JsonPropertyName("configuration")]
    public Dictionary<string, string> Configuration { get; init; } = new(StringComparer.Ordinal);

    [JsonPropertyName("cases")]
    public List<BenchmarkCaseResult> Cases { get; init; } = new();

    [JsonPropertyName("summaries")]
    public Dictionary<string, BenchmarkMetricSummary> Summaries { get; init; } = new(StringComparer.Ordinal);
}

public sealed class BenchmarkSourceInfo
{
    [JsonPropertyName("commitSha")]
    public string? CommitSha { get; init; }

    [JsonPropertyName("imageDigest")]
    public string? ImageDigest { get; init; }
}

public sealed class BenchmarkEnvironmentInfo
{
    [JsonPropertyName("machineName")]
    public string MachineName { get; init; } = string.Empty;

    [JsonPropertyName("operatingSystem")]
    public string OperatingSystem { get; init; } = string.Empty;

    [JsonPropertyName("processArchitecture")]
    public string ProcessArchitecture { get; init; } = string.Empty;

    [JsonPropertyName("framework")]
    public string Framework { get; init; } = string.Empty;

    [JsonPropertyName("processorCount")]
    public int ProcessorCount { get; init; }

    public static BenchmarkEnvironmentInfo Capture() => new()
    {
        MachineName = Environment.MachineName,
        OperatingSystem = RuntimeInformation.OSDescription,
        ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
        Framework = RuntimeInformation.FrameworkDescription,
        ProcessorCount = Environment.ProcessorCount,
    };
}

public sealed class BenchmarkCaseResult
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("correlationId")]
    public string CorrelationId { get; init; } = string.Empty;

    [JsonPropertyName("iteration")]
    public int Iteration { get; init; }

    [JsonPropertyName("warm")]
    public bool Warm { get; init; }

    [JsonPropertyName("succeeded")]
    public bool Succeeded { get; init; } = true;

    [JsonPropertyName("metricsMilliseconds")]
    public Dictionary<string, double> MetricsMilliseconds { get; init; } = new(StringComparer.Ordinal);

    [JsonPropertyName("metrics")]
    public Dictionary<string, double> Metrics { get; init; } = new(StringComparer.Ordinal);

    [JsonPropertyName("events")]
    public List<BenchmarkEventSample> Events { get; init; } = new();

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

public sealed record BenchmarkEventSample(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("elapsedMilliseconds")] double ElapsedMilliseconds);

public sealed class BenchmarkMetricSummary
{
    [JsonPropertyName("count")]
    public int Count { get; init; }

    [JsonPropertyName("minimum")]
    public double? Minimum { get; init; }

    [JsonPropertyName("maximum")]
    public double? Maximum { get; init; }

    [JsonPropertyName("mean")]
    public double? Mean { get; init; }

    [JsonPropertyName("p50")]
    public double? P50 { get; init; }

    [JsonPropertyName("p90")]
    public double? P90 { get; init; }

    [JsonPropertyName("p95")]
    public double? P95 { get; init; }
}

public static class BenchmarkJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(BenchmarkRunResult result) =>
        JsonSerializer.Serialize(result ?? throw new ArgumentNullException(nameof(result)), Options);
}

/// <summary>
/// Records monotonic elapsed times for one correlated pipeline operation.
/// The injected TimeProvider makes event ordering deterministic in tests.
/// </summary>
public sealed class BenchmarkTimeline
{
    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider;
    private readonly long _started;
    private readonly List<BenchmarkEventSample> _events = new();

    public BenchmarkTimeline(string? correlationId = null, TimeProvider? timeProvider = null)
    {
        CorrelationId = string.IsNullOrWhiteSpace(correlationId)
            ? Guid.NewGuid().ToString("N")
            : correlationId;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _started = _timeProvider.GetTimestamp();
    }

    public string CorrelationId { get; }

    public BenchmarkEventSample Record(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("An event name is required.", nameof(name));
        }

        lock (_sync)
        {
            var sample = new BenchmarkEventSample(name,
                _timeProvider.GetElapsedTime(_started, _timeProvider.GetTimestamp()).TotalMilliseconds);
            _events.Add(sample);
            return sample;
        }
    }

    public bool RecordOnce(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("An event name is required.", nameof(name));
        }

        lock (_sync)
        {
            if (_events.Exists(x => string.Equals(x.Name, name, StringComparison.Ordinal)))
            {
                return false;
            }

            _events.Add(new BenchmarkEventSample(name,
                _timeProvider.GetElapsedTime(_started, _timeProvider.GetTimestamp()).TotalMilliseconds));
            return true;
        }
    }

    public IReadOnlyList<BenchmarkEventSample> Snapshot()
    {
        lock (_sync)
        {
            return _events.ToArray();
        }
    }
}
