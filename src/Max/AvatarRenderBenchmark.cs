#nullable enable

using System;

namespace demo;

/// <summary>Read-only render-loop counters exposed only to the opt-in benchmark API.</summary>
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

/// <summary>
/// Optional diagnostics implemented by renderers that can expose timing without changing
/// frame scheduling, RTP timestamps, or media payloads.
/// </summary>
public interface IAvatarRenderBenchmarkSource
{
    event Action? FirstMouthFrameProduced;

    void ResetBenchmarkCounters();

    AvatarRenderBenchmarkSnapshot GetBenchmarkSnapshot();
}
