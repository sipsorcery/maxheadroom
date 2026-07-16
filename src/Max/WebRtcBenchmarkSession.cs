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
            _renderer?.ResetBenchmarkCounters();
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

    public object Snapshot()
    {
        lock (_sync)
        {
            return new
            {
                sessionId = Id,
                armed = IsArmed,
                events = _timeline.Snapshot(),
                renderer = _renderer?.GetBenchmarkSnapshot(),
            };
        }
    }

    private void OnFirstMouthFrame() => Record(BenchmarkEventNames.FirstMouthFrame);
}
