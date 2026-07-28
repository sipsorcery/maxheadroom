using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
namespace AvatarRenderer.Core.LipSync;

/// <summary>
/// A cheap, real-time lip-sync driver: mouth openness comes from RMS energy, and
/// the vowel *shape* is chosen from spectral brightness (zero-crossing rate) — dark
/// energy → open "aa", bright energy → spread "ee/ih". Phonetically approximate but
/// convincing for a talking head, and it runs on any audio (no phoneme data needed).
/// </summary>
public sealed class AmplitudeLipSyncDriver : ILipSyncDriver
{
    /// <summary>RMS below this is treated as silence (closed mouth).</summary>
    public float NoiseFloor { get; init; } = 0.01f;
    /// <summary>Linear gain applied to RMS before clamping to openness 0..1.</summary>
    public float Gain { get; init; } = 11f;
    /// <summary>Zero-crossing rate mapped to full brightness at this value.</summary>
    public float BrightnessScale { get; init; } = 0.14f;

    public VisemeWeights Analyze(ReadOnlySpan<float> w, int sampleRate)
    {
        if (w.Length == 0) return VisemeWeights.Silent;

        // RMS energy.
        double sumSq = 0;
        int crossings = 0;
        float prev = w[0];
        for (int i = 0; i < w.Length; i++)
        {
            float s = w[i];
            sumSq += s * s;
            if ((s >= 0f) != (prev >= 0f)) crossings++;
            prev = s;
        }
        float rms = (float)Math.Sqrt(sumSq / w.Length);
        if (rms < NoiseFloor) return VisemeWeights.Silent;

        float openness = Math.Clamp((rms - NoiseFloor) * Gain, 0f, 1f);

        // Zero-crossing rate → brightness (0 dark .. 1 bright).
        float zcr = (float)crossings / w.Length;
        float b = Math.Clamp(zcr / BrightnessScale, 0f, 1f);

        // Distribute openness across vowel shapes so the total stays ~openness.
        return new VisemeWeights
        {
            Aa = openness * (1f - b),
            Ee = openness * b * 0.7f,
            Ih = openness * b * 0.3f,
            Oh = openness * (1f - b) * 0.25f,
            Ou = 0f,
        };
    }
}
