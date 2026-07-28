using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
namespace AvatarRenderer.Core.Audio;

/// <summary>
/// A source of mono PCM audio as normalized floats (-1..1). Pull-based so the
/// same interface serves a file (WAV) and a live stream (a ring buffer fed by
/// WebRTC/TTS). The render loop reads the window it needs for each frame.
/// </summary>
public interface IAudioSource
{
    int SampleRate { get; }

    /// <summary>
    /// Copies up to <paramref name="buffer"/>.Length samples into it, returning
    /// the count written. 0 means no data available *right now* (live) or EOF (file);
    /// <see cref="IsComplete"/> disambiguates.
    /// </summary>
    int Read(Span<float> buffer);

    /// <summary>True once no more audio will ever arrive (file EOF, stream closed).</summary>
    bool IsComplete { get; }
}
