#nullable enable

using System.Threading.Tasks;
using demo.Performance;

namespace AvatarPipeline.Tests;

public sealed class BenchmarkPipelineTests
{
    [Fact]
    public async Task RunnerRecordsPerCaseMetricsAndSummaries()
    {
        var result = await BenchmarkPipelineRunner.RunAsync(
            BenchmarkPromptSuites.LlmLatencyV1,
            new[] { BenchmarkPromptSuites.LlmLatencyCases[0], BenchmarkPromptSuites.LlmLatencyCases[1] },
            (_, timeline, _) =>
            {
                timeline.Record(BenchmarkEventNames.LlmRequestStarted);
                timeline.Record(BenchmarkEventNames.LlmResponseHeaders);
                timeline.Record(BenchmarkEventNames.LlmFirstToken);
                timeline.Record(BenchmarkEventNames.LlmFirstSentence);
                timeline.Record(BenchmarkEventNames.LlmComplete);
                timeline.Record(BenchmarkEventNames.TtsAudioReady);
                timeline.Record(BenchmarkEventNames.AudioStarted);
                return Task.CompletedTask;
            }, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Cases.Count);
        Assert.False(result.Cases[0].Warm);
        Assert.True(result.Cases[1].Warm);
        Assert.Equal(2, result.Summaries[BenchmarkMetricNames.AudioStarted].Count);
        Assert.All(result.Cases, item => Assert.True(item.Succeeded));
    }

    [Fact]
    public async Task RunnerRetainsFailureWithoutLeakingSecrets()
    {
        var result = await BenchmarkPipelineRunner.RunAsync(
            "test",
            new[] { new BenchmarkPromptCase("failure", "prompt") },
            (_, _, _) => throw new InvalidOperationException("simulated failure api-key=secret"),
            TestContext.Current.CancellationToken);

        Assert.False(result.Cases[0].Succeeded);
        Assert.Equal("InvalidOperationException", result.Cases[0].Error);
        Assert.DoesNotContain("secret", result.Cases[0].Error!, StringComparison.OrdinalIgnoreCase);
    }
}
