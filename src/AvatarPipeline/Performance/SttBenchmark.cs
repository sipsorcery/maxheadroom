#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace demo.Performance;

public sealed record SttBenchmarkCase(
    string Id,
    string SpeakerId,
    string AudioPath,
    string ReferenceText,
    bool Noisy,
    int NoiseSeed,
    double AudioDurationMilliseconds);

public sealed record SttObservation(
    string Transcript,
    double EngineLatencyMilliseconds,
    double EndOfSpeechToFinalMilliseconds,
    double AudioDurationMilliseconds);

public static class SttBenchmarkRunner
{
    public const string SuiteVersion = "stt-v1";

    public static async Task<BenchmarkRunResult> RunAsync(
        IReadOnlyList<SttBenchmarkCase> cases,
        Func<SttBenchmarkCase, BenchmarkTimeline, CancellationToken, Task<SttObservation>> transcribe,
        CancellationToken cancellationToken = default)
    {
        if (cases == null || cases.Count == 0) throw new ArgumentException("At least one STT case is required.", nameof(cases));
        if (transcribe == null) throw new ArgumentNullException(nameof(transcribe));

        var result = new BenchmarkRunResult
        {
            Configuration = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["suite"] = SuiteVersion,
                ["caseCount"] = cases.Count.ToString(CultureInfo.InvariantCulture),
                ["werCerNormalization"] = "lowercase, punctuation collapsed, whitespace normalized",
                ["noisePolicy"] = "deterministic additive white noise at manifest SNR",
            },
        };
        var samples = new Dictionary<string, List<double>>(StringComparer.Ordinal);

        for (var i = 0; i < cases.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var benchmarkCase = cases[i];
            var timeline = new BenchmarkTimeline();
            timeline.Record(BenchmarkEventNames.SpeechEnd);
            SttObservation? observation = null;
            string? error = null;
            var succeeded = true;

            try
            {
                var candidate = await transcribe(benchmarkCase, timeline, cancellationToken).ConfigureAwait(false);
                if (candidate == null) throw new InvalidOperationException("No STT observation was returned.");
                Validate(candidate);
                observation = candidate;
                timeline.RecordOnce(BenchmarkEventNames.SttFinal);
            }
            catch (Exception excp)
            {
                succeeded = false;
                error = excp.GetType().Name;
            }

            var metricsMilliseconds = new Dictionary<string, double>(StringComparer.Ordinal);
            var metrics = new Dictionary<string, double>(StringComparer.Ordinal);
            if (observation != null)
            {
                metricsMilliseconds["stt_engine_latency_ms"] = observation.EngineLatencyMilliseconds;
                metricsMilliseconds["stt_end_of_speech_to_final_ms"] = observation.EndOfSpeechToFinalMilliseconds;
                metrics["wer"] = BenchmarkTextMetrics.WordErrorRate(benchmarkCase.ReferenceText, observation.Transcript);
                metrics["cer"] = BenchmarkTextMetrics.CharacterErrorRate(benchmarkCase.ReferenceText, observation.Transcript);
                if (observation.AudioDurationMilliseconds > 0)
                {
                    metrics["real_time_factor"] = observation.EngineLatencyMilliseconds / observation.AudioDurationMilliseconds;
                }
            }

            AddSamples(samples, metricsMilliseconds);
            AddSamples(samples, metrics);
            result.Cases.Add(new BenchmarkCaseResult
            {
                Name = benchmarkCase.Id,
                CorrelationId = timeline.CorrelationId,
                Iteration = i,
                Warm = i > 0,
                Succeeded = succeeded,
                Error = error,
                MetricsMilliseconds = metricsMilliseconds,
                Metrics = metrics,
                Events = new List<BenchmarkEventSample>(timeline.Snapshot()),
            });
        }

        foreach (var sample in samples)
        {
            result.Summaries[sample.Key] = BenchmarkStatistics.Summarize(sample.Value);
        }
        return result;
    }

    private static void Validate(SttObservation observation)
    {
        if (observation.Transcript == null ||
            !double.IsFinite(observation.EngineLatencyMilliseconds) || observation.EngineLatencyMilliseconds < 0 ||
            !double.IsFinite(observation.EndOfSpeechToFinalMilliseconds) || observation.EndOfSpeechToFinalMilliseconds < 0 ||
            !double.IsFinite(observation.AudioDurationMilliseconds) || observation.AudioDurationMilliseconds < 0)
        {
            throw new ArgumentException("STT observations must contain finite, non-negative timings and a transcript.");
        }
    }

    private static void AddSamples(Dictionary<string, List<double>> samples, Dictionary<string, double> metrics)
    {
        foreach (var metric in metrics)
        {
            if (!samples.TryGetValue(metric.Key, out var values))
            {
                values = new List<double>();
                samples.Add(metric.Key, values);
            }
            values.Add(metric.Value);
        }
    }
}
