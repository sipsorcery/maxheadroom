using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
namespace AvatarRenderer.Core.Audio;

/// <summary>
/// A thread-safe ring buffer fed by a live producer (WebRTC/TTS PCM) and drained
/// by the render loop. This is the streaming counterpart to <see cref="WavAudioSource"/>.
/// Overruns drop the oldest samples so the mouth stays near real time rather than
/// lagging behind accumulated backlog.
/// </summary>
public sealed class PushAudioSource : IAudioSource
{
    private readonly float[] _ring;
    private int _writeCount; // total samples ever written
    private int _readCount;  // total samples ever read
    private readonly object _gate = new();
    private volatile bool _complete;

    public int SampleRate { get; }
    public bool IsComplete { get { lock (_gate) return _complete && Available == 0; } }

    /// <param name="capacitySamples">Ring size; ~0.5–1s of audio is plenty for lip-sync.</param>
    public PushAudioSource(int sampleRate, int capacitySamples)
    {
        SampleRate = sampleRate;
        _ring = new float[Math.Max(1024, capacitySamples)];
    }

    private int Available => _writeCount - _readCount;

    /// <summary>Producer pushes decoded mono PCM here.</summary>
    public void Write(ReadOnlySpan<float> samples)
    {
        lock (_gate)
        {
            foreach (var s in samples)
            {
                _ring[_writeCount % _ring.Length] = s;
                _writeCount++;
                // Drop oldest if the consumer fell behind.
                if (_writeCount - _readCount > _ring.Length)
                    _readCount = _writeCount - _ring.Length;
            }
        }
    }

    /// <summary>Signals that no further audio will be written.</summary>
    public void Complete() { lock (_gate) _complete = true; }

    public int Read(Span<float> buffer)
    {
        lock (_gate)
        {
            int n = Math.Min(buffer.Length, Available);
            for (int i = 0; i < n; i++)
                buffer[i] = _ring[(_readCount + i) % _ring.Length];
            _readCount += n;
            return n;
        }
    }
}
