using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using AvatarRenderer.Core.Animation;
using AvatarRenderer.Core.Audio;

namespace AvatarRenderer.Core.LipSync;

/// <summary>
/// Drives a <see cref="Pose"/>'s mouth from an audio stream each frame: pulls the
/// audio elapsed since the last frame into a rolling analysis window, runs the
/// pluggable <see cref="ILipSyncDriver"/>, applies attack/release smoothing, and
/// writes the result through a <see cref="VisemeMapper"/>. Works identically for a
/// WAV file or a live <see cref="PushAudioSource"/>.
/// </summary>
public sealed class LipSyncAnimator
{
    private readonly IAudioSource _source;
    private readonly ILipSyncDriver _driver;
    private readonly VisemeMapper _mapper;

    private readonly float[] _window;   // rolling analysis window
    private int _windowFilled;
    private readonly float[] _scratch;

    private VisemeWeights _current;

    // Smoothing time constants (seconds): open fast, close a little slower.
    private readonly double _attackTau;
    private readonly double _releaseTau;

    public LipSyncAnimator(
        IAudioSource source, ILipSyncDriver driver, VisemeMapper mapper,
        double windowMs = 45, double attackTau = 0.025, double releaseTau = 0.08)
    {
        _source = source;
        _driver = driver;
        _mapper = mapper;
        int windowSamples = Math.Max(1, (int)(source.SampleRate * windowMs / 1000.0));
        _window = new float[windowSamples];
        _scratch = new float[windowSamples];
        _attackTau = attackTau;
        _releaseTau = releaseTau;
    }

    public VisemeWeights Current => _current;

    /// <summary>Advances by <paramref name="dt"/> seconds, updating <paramref name="pose"/>'s mouth. Returns false once audio is exhausted and the mouth has closed.</summary>
    public bool Update(Pose pose, double dt)
    {
        int consume = Math.Clamp((int)Math.Round(_source.SampleRate * dt), 0, _window.Length);
        if (consume > 0)
        {
            int got = _source.Read(_scratch.AsSpan(0, consume));

            // Shift window left by `consume` and append the new samples (zero-filled on underrun).
            int keep = _window.Length - consume;
            if (keep > 0) Array.Copy(_window, consume, _window, 0, keep);
            _scratch.AsSpan(0, got).CopyTo(_window.AsSpan(keep, got));
            if (got < consume) _window.AsSpan(keep + got, consume - got).Clear();
            _windowFilled = Math.Min(_window.Length, _windowFilled + consume);
        }

        var target = _driver.Analyze(_window.AsSpan(0, _windowFilled), _source.SampleRate);

        // Asymmetric smoothing: faster when opening than when closing.
        bool opening = Total(target) >= Total(_current);
        double tau = opening ? _attackTau : _releaseTau;
        float alpha = (float)(1.0 - Math.Exp(-dt / Math.Max(1e-4, tau)));
        _current = _current.LerpTo(target, alpha);

        _mapper.Apply(pose, _current);

        return !(_source.IsComplete && Total(_current) < 0.01f);
    }

    private static float Total(in VisemeWeights w) => w.Aa + w.Ih + w.Ou + w.Ee + w.Oh;
}
