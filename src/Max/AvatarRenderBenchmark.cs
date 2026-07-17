#nullable enable

using System;

namespace demo;

public sealed record AvatarRenderBenchmarkSnapshot(
    long EmittedFrames,
    long DroppedTicks,
    double EffectiveFramesPerSecond,
    long InferenceCount,
    double MeanInferenceMilliseconds,
    double MaximumInferenceMilliseconds,
    long EncodeCount,
    double MeanEncodeMilliseconds,
    double MaximumEncodeMilliseconds,
    double MeanMouthFrameLatenessMilliseconds,
    double MaximumMouthFrameLatenessMilliseconds);

public interface IAvatarRenderBenchmarkSource
{
    event Action? FirstMouthFrameProduced;
    void ResetBenchmarkCounters();
    AvatarRenderBenchmarkSnapshot GetBenchmarkSnapshot();
}
