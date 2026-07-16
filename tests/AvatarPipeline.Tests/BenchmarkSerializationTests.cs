using System.Text.Json;
using demo.Performance;

namespace AvatarPipeline.Tests;

public sealed class BenchmarkSerializationTests
{
    [Fact]
    public void Serialize_UsesVersionedCamelCaseContractWithoutNullNumbers()
    {
        var result = new BenchmarkRunResult
        {
            RunId = "run-1",
            Source = new BenchmarkSourceInfo
            {
                CommitSha = "abc123",
                ImageDigest = "sha256:123",
            },
            Cases =
            {
                new BenchmarkCaseResult
                {
                    Name = "llm-warm",
                    CorrelationId = "request-1",
                    Iteration = 1,
                    Warm = true,
                    MetricsMilliseconds =
                    {
                        ["time_to_first_token"] = 125.5,
                    },
                },
            },
            Summaries =
            {
                ["time_to_first_token"] = BenchmarkStatistics.Summarize(new[] { 125.5 }),
                ["empty"] = BenchmarkStatistics.Summarize(Array.Empty<double>()),
            },
        };

        var json = BenchmarkJson.Serialize(result);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(BenchmarkSchema.CurrentVersion, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("run-1", root.GetProperty("runId").GetString());
        Assert.Equal("abc123", root.GetProperty("source").GetProperty("commitSha").GetString());
        Assert.Equal(125.5,
            root.GetProperty("summaries").GetProperty("time_to_first_token").GetProperty("p95").GetDouble());
        Assert.Equal(0, root.GetProperty("summaries").GetProperty("empty").GetProperty("count").GetInt32());
        Assert.False(root.GetProperty("summaries").GetProperty("empty").TryGetProperty("p95", out _));
        Assert.False(root.GetProperty("cases")[0].TryGetProperty("error", out _));
    }
}
