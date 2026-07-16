using demo.Performance;

namespace AvatarPipeline.Tests;

public sealed class BenchmarkTimelineTests
{
    [Fact]
    public void Timeline_RecordsDeterministicMonotonicEvents()
    {
        var time = new ManualTimeProvider();
        var timeline = new BenchmarkTimeline("request-42", time);

        time.Advance(TimeSpan.FromMilliseconds(12.5));
        timeline.Record(BenchmarkEventNames.LlmRequestStarted);
        time.Advance(TimeSpan.FromMilliseconds(37.5));
        timeline.Record(BenchmarkEventNames.LlmFirstToken);

        var events = timeline.Snapshot();
        Assert.Equal("request-42", timeline.CorrelationId);
        Assert.Collection(events,
            first =>
            {
                Assert.Equal(BenchmarkEventNames.LlmRequestStarted, first.Name);
                Assert.Equal(12.5d, first.ElapsedMilliseconds, 10);
            },
            second =>
            {
                Assert.Equal(BenchmarkEventNames.LlmFirstToken, second.Name);
                Assert.Equal(50d, second.ElapsedMilliseconds, 10);
            });
    }

    [Fact]
    public void RecordOnce_KeepsFirstObservation()
    {
        var time = new ManualTimeProvider();
        var timeline = new BenchmarkTimeline(timeProvider: time);

        time.Advance(TimeSpan.FromMilliseconds(10));
        Assert.True(timeline.RecordOnce(BenchmarkEventNames.LlmFirstToken));
        time.Advance(TimeSpan.FromMilliseconds(20));
        Assert.False(timeline.RecordOnce(BenchmarkEventNames.LlmFirstToken));

        var sample = Assert.Single(timeline.Snapshot());
        Assert.Equal(10d, sample.ElapsedMilliseconds, 10);
    }

    [Fact]
    public void EventNames_AreUniqueSnakeCaseIdentifiers()
    {
        Assert.Equal(BenchmarkEventNames.All.Count, BenchmarkEventNames.All.Distinct().Count());
        Assert.All(BenchmarkEventNames.All, name =>
            Assert.Matches("^[a-z][a-z0-9]*(?:_[a-z0-9]+)*$", name));
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => 1_000_000;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan duration) =>
            _timestamp += (long)(duration.TotalSeconds * TimestampFrequency);
    }
}
