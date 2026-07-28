using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
namespace AvatarRenderer.Core.LipSync;

/// <summary>
/// Converts a window of audio into viseme weights for one frame. This is the
/// pluggable seam: an amplitude/energy driver ships first; a phoneme-timeline
/// driver (from TTS phoneme timings) can implement the same interface later
/// without touching the animator or renderer.
/// </summary>
public interface ILipSyncDriver
{
    /// <param name="audioWindow">Most-recent mono samples aligned to the current frame.</param>
    /// <param name="sampleRate">Samples per second of <paramref name="audioWindow"/>.</param>
    VisemeWeights Analyze(ReadOnlySpan<float> audioWindow, int sampleRate);
}
