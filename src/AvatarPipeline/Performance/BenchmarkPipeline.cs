#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace demo.Performance;

public static class BenchmarkMetricNames
{
    public const string LlmRequestStart = "llm_request_start_ms";
    public const string LlmResponseHeaders = "llm_response_headers_ms";
    public const string LlmFirstToken = "llm_first_token_ms";
    public const string LlmFirstSentence = "llm_first_sentence_ms";
    public const string LlmComplete = "llm_complete_ms";
    public const string TtsAudioReady = "tts_audio_ready_ms";
    public const string AudioStarted = "audio_started_ms";
}

public sealed record BenchmarkPromptCase(string Name, string Prompt);

public static class BenchmarkPromptSuites
{
    public const string LlmLatencyV1 = "llm-latency-v1";

    public static IReadOnlyList<BenchmarkPromptCase> LlmLatencyCases { get; } = new[]
    {
        new BenchmarkPromptCase("short_fact", "Name one thing television got right."),
        new BenchmarkPromptCase("creative", "Give me a witty one-sentence slogan for a robot that hates meetings."),
        new BenchmarkPromptCase("technical", "In one short sentence, explain why buffering increases latency."),
        new BenchmarkPromptCase("follow_up", "What would Max say about a queue that never drains?"),
    };
}

public static class BenchmarkTimelineMetrics
{
    public static Dictionary<string, double> FromTimeline(BenchmarkTimeline timeline)
    {
        if (timeline == null) throw new ArgumentNullException(nameof(timeline));

        var events = timeline.Snapshot();
        var origin = Find(events, BenchmarkEventNames.PromptAccepted)
            ?? Find(events, BenchmarkEventNames.SpeechEnd);
        if (origin == null)
        {
            return new Dictionary<string, double>(StringComparer.Ordinal);
        }

        var metrics = new Dictionary<string, double>(StringComparer.Ordinal);
        AddDelta(metrics, BenchmarkMetricNames.LlmRequestStart, origin, Find(events, BenchmarkEventNames.LlmRequestStarted));
        AddDelta(metrics, BenchmarkMetricNames.LlmResponseHeaders, origin, Find(events, BenchmarkEventNames.LlmResponseHeaders));
        AddDelta(metrics, BenchmarkMetricNames.LlmFirstToken, origin, Find(events, BenchmarkEventNames.LlmFirstToken));
        AddDelta(metrics, BenchmarkMetricNames.LlmFirstSentence, origin, Find(events, BenchmarkEventNames.LlmFirstSentence));
        AddDelta(metrics, BenchmarkMetricNames.LlmComplete, origin, Find(events, BenchmarkEventNames.LlmComplete));
        AddDelta(metrics, BenchmarkMetricNames.TtsAudioReady, origin, Find(events, BenchmarkEventNames.TtsAudioReady));
        AddDelta(metrics, BenchmarkMetricNames.AudioStarted, origin, Find(events, BenchmarkEventNames.AudioStarted));
        return metrics;
    }

    private static BenchmarkEventSample? Find(IReadOnlyList<BenchmarkEventSample> events, string name)
    {
        for (var i = 0; i < events.Count; i++)
        {
            if (string.Equals(events[i].Name, name, StringComparison.Ordinal))
            {
                return events[i];
            }
        }

        return null;
    }

    private static void AddDelta(Dictionary<string, double> metrics, string name,
        BenchmarkEventSample origin, BenchmarkEventSample? target)
    {
        if (target != null && target.ElapsedMilliseconds >= origin.ElapsedMilliseconds)
        {
            metrics[name] = target.ElapsedMilliseconds - origin.ElapsedMilliseconds;
        }
    }
}

/// <summary>
/// Runs a deterministic prompt suite against an injected pipeline. The injected executor is
/// where the live WebRTC application records LLM and audio events; keeping orchestration here
/// makes the result contract testable without network calls or API credentials.
/// </summary>
public static class BenchmarkPipelineRunner
{
    public static async Task<BenchmarkRunResult> RunAsync(
        string suite,
        IReadOnlyList<BenchmarkPromptCase> cases,
        Func<BenchmarkPromptCase, BenchmarkTimeline, CancellationToken, Task> execute,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(suite)) throw new ArgumentException("A suite name is required.", nameof(suite));
        if (cases == null || cases.Count == 0) throw new ArgumentException("At least one benchmark case is required.", nameof(cases));
        if (execute == null) throw new ArgumentNullException(nameof(execute));

        var result = new BenchmarkRunResult
        {
            Configuration = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["suite"] = suite,
                ["caseCount"] = cases.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["warmClassification"] = "first case observed separately; subsequent cases marked warm",
            },
        };

        var samplesByMetric = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        for (var i = 0; i < cases.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var benchmarkCase = cases[i];
            var timeline = new BenchmarkTimeline();
            timeline.Record(BenchmarkEventNames.PromptAccepted);

            string? error = null;
            var succeeded = true;
            try
            {
                await execute(benchmarkCase, timeline, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception excp)
            {
                succeeded = false;
                // Exception messages can contain request URLs, headers, or provider details.
                // Keep only the stable type name in persisted benchmark artifacts.
                error = excp.GetType().Name;
            }

            var metrics = BenchmarkTimelineMetrics.FromTimeline(timeline);
            foreach (var metric in metrics)
            {
                if (!samplesByMetric.TryGetValue(metric.Key, out var values))
                {
                    values = new List<double>();
                    samplesByMetric.Add(metric.Key, values);
                }
                values.Add(metric.Value);
            }

            result.Cases.Add(new BenchmarkCaseResult
            {
                Name = benchmarkCase.Name,
                CorrelationId = timeline.CorrelationId,
                Iteration = i,
                Warm = i > 0,
                Succeeded = succeeded,
                Error = error,
                MetricsMilliseconds = metrics,
                Events = new List<BenchmarkEventSample>(timeline.Snapshot()),
            });
        }

        foreach (var metric in samplesByMetric)
        {
            result.Summaries[metric.Key] = BenchmarkStatistics.Summarize(metric.Value);
        }

        return result;
    }
}
