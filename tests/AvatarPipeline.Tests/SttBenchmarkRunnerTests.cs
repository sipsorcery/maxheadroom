#nullable enable

using System.Threading.Tasks;
using demo.Performance;

namespace AvatarPipeline.Tests;

public sealed class SttBenchmarkRunnerTests
{
    [Fact]
    public async Task RunnerReportsPerUtteranceWerCerAndLatencySummaries()
    {
        var cases = new[]
        {
            new SttBenchmarkCase("one", "speaker-a", "one.wav", "hello world", false, 0, 1000),
            new SttBenchmarkCase("two", "speaker-b", "two-noisy.wav", "hello world", true, 42, 1000),
        };

        var result = await SttBenchmarkRunner.RunAsync(cases, (benchmarkCase, timeline, _) =>
        {
            timeline.Record(BenchmarkEventNames.SttFinal);
            return Task.FromResult(new SttObservation(
                benchmarkCase.Id == "one" ? "hello world" : "hello word",
                250,
                80,
                benchmarkCase.AudioDurationMilliseconds));
        }, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Cases.Count);
        Assert.Equal(0d, result.Cases[0].Metrics["wer"]);
        Assert.Equal(0.5d, result.Cases[1].Metrics["wer"]);
        Assert.Equal(2, result.Summaries["cer"].Count);
        Assert.Equal(2, result.Summaries["stt_end_of_speech_to_final_ms"].Count);
    }
}
