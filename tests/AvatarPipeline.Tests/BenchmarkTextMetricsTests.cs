#nullable enable

using demo.Performance;

namespace AvatarPipeline.Tests;

public sealed class BenchmarkTextMetricsTests
{
    [Fact]
    public void WordErrorRateNormalizesCaseAndPunctuation()
    {
        Assert.Equal(0d, BenchmarkTextMetrics.WordErrorRate("Hello, WORLD!", "hello world"));
    }

    [Fact]
    public void WordErrorRateCountsInsertionDeletionAndSubstitution()
    {
        Assert.Equal(0.5d, BenchmarkTextMetrics.WordErrorRate("one two", "one three"));
    }

    [Fact]
    public void CharacterErrorRateUsesCharactersWithoutSpaces()
    {
        Assert.Equal(0.25d, BenchmarkTextMetrics.CharacterErrorRate("abcd", "abxd"));
    }

    [Fact]
    public void EmptyReferenceIsHandledDeterministically()
    {
        Assert.Equal(0d, BenchmarkTextMetrics.WordErrorRate("", "   "));
        Assert.Equal(1d, BenchmarkTextMetrics.WordErrorRate("", "audio"));
    }

    [Fact]
    public void NoiseTransformIsDeterministicAndLeavesInputUntouched()
    {
        var input = new short[] { 1000, -500, 250, 0, 900 };
        var first = SttNoise.AdditiveWhiteNoise(input, 20, 3101);
        var second = SttNoise.AdditiveWhiteNoise(input, 20, 3101);

        Assert.Equal(first, second);
        Assert.Equal(new short[] { 1000, -500, 250, 0, 900 }, input);
        Assert.NotEqual(input, first);
    }
}
