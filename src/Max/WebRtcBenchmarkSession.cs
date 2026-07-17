#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace demo;

/// <summary>Correlated timing and media state for one deterministic WebRTC benchmark peer.</summary>
public sealed class WebRtcBenchmarkSession
{
    private readonly object _sync = new();
    private readonly Stopwatch _clock = new();
    private readonly List<BenchmarkEvent> _events = new();
    private IAvatarRenderBenchmarkSource? _renderer;
    private long _receivedAudioFrames;
    private long _receivedAudioSamples;
    private double _receivedAudioSumSquares;
    private int _receivedAudioPeak;

    public WebRtcBenchmarkSession(string id) => Id = id;

    public string Id { get; }
    public bool IsArmed { get; private set; }

    public void Attach(IAvatarRenderer renderer)
    {
        if (renderer is not IAvatarRenderBenchmarkSource source) return;
        lock (_sync)
        {
            _renderer = source;
            source.FirstMouthFrameProduced += () => Record("first_mouth_frame");
        }
    }

    public void Arm()
    {
        lock (_sync)
        {
            _events.Clear();
            _receivedAudioFrames = 0;
            _receivedAudioSamples = 0;
            _receivedAudioSumSquares = 0;
            _receivedAudioPeak = 0;
            _renderer?.ResetBenchmarkCounters();
            _clock.Restart();
            IsArmed = true;
            BenchMetrics.SetBenchmarkEventSink(Record);
        }
    }

    public void Record(string name)
    {
        lock (_sync)
        {
            if (!IsArmed || _events.Any(item => item.Name == name)) return;
            _events.Add(new BenchmarkEvent(name, _clock.Elapsed.TotalMilliseconds));
        }
    }

    public void RecordReceivedAudio(short[] pcm)
    {
        if (pcm.Length == 0) return;
        lock (_sync)
        {
            if (!IsArmed) return;
            _receivedAudioFrames++;
            _receivedAudioSamples += pcm.Length;
            foreach (var sample in pcm)
            {
                _receivedAudioSumSquares += (double)sample * sample;
                _receivedAudioPeak = Math.Max(_receivedAudioPeak, Math.Abs((int)sample));
            }
        }
    }

    public object Snapshot()
    {
        lock (_sync)
        {
            return new
            {
                sessionId = Id,
                armed = IsArmed,
                events = _events.ToArray(),
                receivedAudio = new
                {
                    frames = _receivedAudioFrames,
                    samples = _receivedAudioSamples,
                    rms = _receivedAudioSamples == 0 ? 0 : Math.Sqrt(_receivedAudioSumSquares / _receivedAudioSamples),
                    peak = _receivedAudioPeak,
                },
                renderer = _renderer?.GetBenchmarkSnapshot(),
            };
        }
    }

    public sealed record BenchmarkEvent(string Name, double ElapsedMilliseconds);
}
