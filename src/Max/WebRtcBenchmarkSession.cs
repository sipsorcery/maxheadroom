#nullable enable

using System;
using demo.Performance;

namespace demo;

/// <summary>State shared by the opt-in benchmark HTTP API and one WebRTC peer.</summary>
public sealed class WebRtcBenchmarkSession
{
    private readonly object _sync = new();
    private BenchmarkTimeline _timeline = new();
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
            source.FirstMouthFrameProduced += OnFirstMouthFrame;
        }
    }

    public void Arm()
    {
        lock (_sync)
        {
            _timeline = new BenchmarkTimeline(Id);
            IsArmed = true;
            _receivedAudioFrames = 0;
            _receivedAudioSamples = 0;
            _receivedAudioSumSquares = 0;
            _receivedAudioPeak = 0;
            _renderer?.ResetBenchmarkCounters();
            BenchMetrics.SetBenchmarkEventSink(Record);
        }
    }

    public void Record(string eventName)
    {
        lock (_sync)
        {
            if (IsArmed) _timeline.RecordOnce(eventName);
        }
    }

    public BenchmarkTimeline? Timeline
    {
        get
        {
            lock (_sync) return IsArmed ? _timeline : null;
        }
    }

    /// <summary>Records decoded microphone PCM at the WebRTC/STT boundary.</summary>
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
                events = _timeline.Snapshot(),
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

    private void OnFirstMouthFrame() => Record(BenchmarkEventNames.FirstMouthFrame);
}
