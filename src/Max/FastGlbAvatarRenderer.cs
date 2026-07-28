//-----------------------------------------------------------------------------
// Filename: FastGlbAvatarRenderer.cs
//
// Description: A glTF/GLB/VRM avatar video source backed by a fast, pure-managed
// CPU rasterizer (the AvatarRenderer.Core pipeline under FastAvatar/). Unlike
// CpuGlbAvatarRenderer - which re-renders the whole model from scratch every
// frame via a static Skia call - this keeps the skinned model, skeleton and
// morph targets resident and only re-poses + re-rasterizes each frame, with the
// rasterizer parallelized across cores. That makes real-time skeletal animation,
// blendshape lip-sync and procedural liveness affordable on CPU-only hosts.
//
// Lip-sync is amplitude-driven (RMS -> mouth openness, spectral brightness ->
// vowel shape) via the pluggable ILipSyncDriver; it needs no phoneme data, so it
// works with any TTS. Blinking and a gentle breathing sway are added procedurally
// so the face never looks frozen.
//
// It implements the same IAvatarRenderer (IAvatarMouth + IVideoSource) seam as the
// other renderers, so the speaker and peer-connection wiring are unchanged.
//
// Author(s):
// Aaron Clauson (aaron@sipsorcery.com)
//
// License:
// BSD 3-Clause "New" or "Revised" License, see included LICENSE.md file.
//-----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using AvatarRenderer.Core.Animation;
using AvatarRenderer.Core.Audio;
using AvatarRenderer.Core.LipSync;
using AvatarRenderer.Core.Loading;
using AvatarRenderer.Core.Rendering;
using AvatarRenderer.Core.Scene;
using Microsoft.Extensions.Logging;
using SIPSorceryMedia.Abstractions;
using CoreCamera = AvatarRenderer.Core.Rendering.Camera;

namespace demo;

public sealed class FastGlbAvatarRenderer : IAvatarRenderer, IVisemeTimelineReceiver
{
    public const int WIDTH = 480;
    public const int HEIGHT = 360;
    private const int DefaultFrameRate = 20;
    private const int VideoSamplingRate = 90000;
    private const int H264SuggestedFormatId = 100;

    public static readonly List<VideoFormat> SupportedFormats = new()
    {
        new VideoFormat(VideoCodecsEnum.H264, H264SuggestedFormatId, VideoSamplingRate, "packetization-mode=1")
    };

    private static readonly ILogger Logger = SIPSorcery.LogFactory.CreateLogger<FastGlbAvatarRenderer>();
    private readonly MediaFormatManager<VideoFormat> _formatManager = new(SupportedFormats);
    private readonly IVideoEncoder _videoEncoder;

    // ---- Core rendering pipeline (resident) ----
    private readonly SkinnedModel _model;
    private readonly PoseEvaluator _evaluator;
    private readonly VisemeMapper _mapper;
    private readonly LivenessAnimator _liveness;
    private readonly ILipSyncDriver _driver = new AmplitudeLipSyncDriver();
    private readonly Pose _pose = new();
    private readonly SoftwareRasterizer _raster = new();
    private readonly Framebuffer _fb = new(WIDTH, HEIGHT);

    // ---- Camera + triggered actions ----
    // A spoken action word (e.g. "jump") plays the matching .vrma: the camera zooms out
    // to full body, the clip runs once, then it zooms back to the face.
    private enum ActionPhase { Idle, ZoomOut, Play, ZoomIn }
    private const double ZoomDurationSec = 0.6;
    private CoreCamera _faceCamera = null!, _bodyCamera = null!, _activeCamera = null!;
    private readonly Dictionary<string, VrmaPlayer> _actions = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<int, NodeTransform> _baseOverrides = new();
    private volatile VrmaPlayer _pendingAction;
    private volatile string _pendingActionName;
    private ActionPhase _phase = ActionPhase.Idle;
    private VrmaPlayer _action;
    private double _actionTime, _phaseStart;
    private float _zoom; // 0 = face, 1 = full body

    // ---- Mouth state ----
    // Lip-sync uses the TTS phoneme timeline for the mouth *shape* when one is supplied
    // (IVisemeTimelineReceiver), and the audio RMS for the *openness*, so intra-word
    // pauses close the mouth. With no timeline it falls back to the pure amplitude driver.
    private const double AttackTau = 0.025, ReleaseTau = 0.08;
    private readonly object _mouthLock = new();
    private IReadOnlyList<VisemeCue> _cues = Array.Empty<VisemeCue>();
    private bool _speaking;
    private VisemeWeights _ampTarget;    // amplitude-derived viseme weights (fallback shape)
    private float _ampOpenness;          // amplitude-derived mouth openness 0..1
    private double _pushedAudioSeconds;  // total audio fed this utterance (leading-edge clock)
    private double _lastPushWallSeconds;
    private VisemeWeights _currentMouth; // smoothed, render-thread only

    // ---- Video source plumbing ----
    private readonly byte[] _bgrFrame = new byte[WIDTH * HEIGHT * 3];
    private readonly object _frameLock = new();
    private readonly Timer _renderTimer;
    private readonly int _frameRate;
    private readonly int _frameSpacingMs;
    private double _lastTickSeconds;
    private int _renderInProgress;
    private int _frameCount;
    private bool _isStarted, _isPaused, _isClosed, _faulted;

    public FastGlbAvatarRenderer(string modelPath, IVideoEncoder encoder = null, bool rotate180 = false, string vrmaDir = null)
    {
        _videoEncoder = encoder;
        _frameRate = ReadFrameRate();
        _frameSpacingMs = Math.Max(1, 1000 / _frameRate);

        _model = GltfLoader.Load(modelPath);
        _evaluator = new PoseEvaluator(_model);
        _mapper = new VisemeMapper(_model);
        _liveness = new LivenessAnimator(_model);
        if (rotate180) { _pose.ModelTransform = Matrix4x4.CreateRotationY(MathF.PI); }

        // Relax the arms from the authored T-pose down to the sides (persistent base pose;
        // liveness sway and lip-sync layer on top on other bones).
        var relaxed = RestPoseBuilder.RelaxArms(_model, _pose);
        _baseOverrides = new Dictionary<int, NodeTransform>(_pose.NodeOverrides);

        var neutral = _evaluator.Evaluate(_pose);
        _faceCamera = BuildCamera(_model, neutral, (float)WIDTH / HEIGHT);
        _bodyCamera = CoreCamera.FrameModel(neutral, (float)WIDTH / HEIGHT);
        _activeCamera = _faceCamera;
        LoadActions(vrmaDir, modelPath, rotate180);
        RenderInto(neutral);

        Logger.LogInformation("FastGlbAvatarRenderer loaded {Model} ({Tris} tris); visemes {Map}; relaxed arms [{Arms}]; actions [{Actions}].",
            System.IO.Path.GetFileName(modelPath), TriangleCount(neutral),
            string.Join(",", _mapper.Resolved.Keys), string.Join(",", relaxed), string.Join(",", _actions.Keys));

        _renderTimer = new Timer(RenderFrame, null, Timeout.Infinite, Timeout.Infinite);
        _lastTickSeconds = NowSeconds();
    }

    private void LoadActions(string vrmaDir, string modelPath, bool rotate180)
    {
        if (string.IsNullOrWhiteSpace(vrmaDir) || !Directory.Exists(vrmaDir)) return;
        var targetBones = VrmHumanoid.Parse(modelPath);
        if (targetBones.Count == 0) return;
        foreach (var file in Directory.EnumerateFiles(vrmaDir, "*.vrma"))
        {
            try
            {
                // rotate180 models are VRM 0.x (face -Z); the clips are VRM-1.0 authored,
                // so the player mirrors the motion to match.
                var player = new VrmaPlayer(_model, targetBones, new VrmaClip(file), reversedFacing: rotate180);
                _actions[Path.GetFileNameWithoutExtension(file)] = player;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to load VRMA {File}.", file);
            }
        }
    }

    /// <summary>Action names available for this model (from the bundled .vrma clips).</summary>
    public IReadOnlyCollection<string> ActionNames => _actions.Keys;

    /// <summary>
    /// Requests an action if <paramref name="word"/> matches (or prefixes) a clip name -
    /// e.g. "jump", "clap" (Clapping), "look" (LookAround). Thread-safe; the render loop
    /// starts it when idle. Returns true if a match was queued.
    /// </summary>
    public bool TriggerAction(string word)
    {
        if (string.IsNullOrWhiteSpace(word)) return false;
        word = word.Trim();
        foreach (var name in _actions.Keys)
        {
            if (name.Equals(word, StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith(word, StringComparison.OrdinalIgnoreCase))
            {
                _pendingActionName = name;
                _pendingAction = _actions[name];
                return true;
            }
        }
        return false;
    }

    // ---- IAvatarMouth ----

    public bool PacesAudioInternally => false;

    public void SetVisemeTimeline(IReadOnlyList<VisemeCue> cues)
    {
        lock (_mouthLock) { _cues = cues ?? Array.Empty<VisemeCue>(); }
    }

    public void BeginSpeech()
    {
        double now = NowSeconds();
        lock (_mouthLock)
        {
            _speaking = true;
            _pushedAudioSeconds = 0;
            _lastPushWallSeconds = now;
            _ampTarget = VisemeWeights.Silent;
            _ampOpenness = 0;
        }
    }

    public void PushAudio(ReadOnlySpan<short> pcm16, int sampleRate)
    {
        if (pcm16.Length == 0) { return; }

        Span<float> buf = pcm16.Length <= 4096 ? stackalloc float[pcm16.Length] : new float[pcm16.Length];
        for (int i = 0; i < pcm16.Length; i++) { buf[i] = pcm16[i] / 32768f; }

        VisemeWeights vw = _driver.Analyze(buf, sampleRate);
        float openness = Math.Clamp(vw.Aa + vw.Ih + vw.Ou + vw.Ee + vw.Oh, 0f, 1f);
        double now = NowSeconds();
        lock (_mouthLock)
        {
            _ampTarget = vw;
            _ampOpenness = openness;
            _pushedAudioSeconds += (double)pcm16.Length / sampleRate;
            _lastPushWallSeconds = now;
        }
    }

    public void EndSpeech()
    {
        lock (_mouthLock)
        {
            _speaking = false;
            _ampTarget = VisemeWeights.Silent;
            _ampOpenness = 0;
            _cues = Array.Empty<VisemeCue>();
        }
    }

    // Computes the mouth for this frame and writes it into the pose. Shape from the
    // phoneme timeline (leading-edge audio clock, so it's lead-compensated without the
    // renderer knowing the speaker's lead), openness from audio RMS.
    private void UpdateMouth(double now, double dt)
    {
        bool speaking;
        IReadOnlyList<VisemeCue> cues;
        VisemeWeights ampTarget;
        float openness;
        double pushed, lastPush;
        lock (_mouthLock)
        {
            speaking = _speaking; cues = _cues; ampTarget = _ampTarget;
            openness = _ampOpenness; pushed = _pushedAudioSeconds; lastPush = _lastPushWallSeconds;
        }

        VisemeWeights target;
        if (speaking && cues.Count > 0)
        {
            double t = pushed + Math.Max(0.0, now - lastPush);
            target = Scale(MapCueAt(cues, t), openness);
        }
        else if (speaking)
        {
            target = ampTarget; // no timeline supplied: pure amplitude shaping
        }
        else
        {
            target = VisemeWeights.Silent;
        }

        double tau = Total(target) >= Total(_currentMouth) ? AttackTau : ReleaseTau;
        float alpha = (float)(1.0 - Math.Exp(-dt / Math.Max(1e-4, tau)));
        _currentMouth = _currentMouth.LerpTo(target, alpha);
        _mapper.Apply(_pose, _currentMouth);
    }

    // ---- render loop ----

    private void RenderFrame(object state)
    {
        bool hasRawSubscribers = OnVideoSourceRawSample != null;
        bool hasEncodedSubscribers = _videoEncoder != null && OnVideoSourceEncodedSample != null && !_formatManager.SelectedFormat.IsEmpty();
        if (_isClosed || _isPaused || _faulted || (!hasRawSubscribers && !hasEncodedSubscribers)) { return; }
        if (Interlocked.CompareExchange(ref _renderInProgress, 1, 0) != 0) { return; } // skip if a frame is still in flight

        try
        {
            double now = NowSeconds();
            double dt = Math.Clamp(now - _lastTickSeconds, 1e-4, 0.25);
            _lastTickSeconds = now;

            UpdateBodyAndCamera(now, dt);
            UpdateMouth(now, dt);

            var model = _evaluator.Evaluate(_pose);
            RenderInto(model);

            byte[] frame;
            lock (_frameLock) { frame = (byte[])_bgrFrame.Clone(); }
            OnVideoSourceRawSample?.Invoke((uint)_frameSpacingMs, WIDTH, HEIGHT, frame, VideoPixelFormatsEnum.Bgr);
            if (hasEncodedSubscribers)
            {
                var encoded = _videoEncoder.EncodeVideo(WIDTH, HEIGHT, frame, VideoPixelFormatsEnum.Bgr, _formatManager.SelectedFormat.Codec);
                if (encoded != null)
                {
                    OnVideoSourceEncodedSample?.Invoke((uint)(VideoSamplingRate / _frameRate), encoded);
                }
            }

            if ((++_frameCount % (_frameRate * 5)) == 0)
            {
                Logger.LogDebug("FastGlb frame {Frame}.", _frameCount);
            }
        }
        catch (Exception excp)
        {
            _faulted = true;
            _renderTimer.Change(Timeout.Infinite, Timeout.Infinite);
            Logger.LogError(excp, "Fatal error in FastGlb render loop after frame {Frame}.", _frameCount);
            OnVideoSourceError?.Invoke(excp.Message);
        }
        finally
        {
            Volatile.Write(ref _renderInProgress, 0);
        }
    }

    // Drives idle liveness or a triggered action, and the face↔body camera zoom.
    private void UpdateBodyAndCamera(double now, double dt)
    {
        if (_phase == ActionPhase.Idle && _pendingAction is { } pending)
        {
            _action = pending;
            _pendingAction = null;
            _phase = ActionPhase.ZoomOut;
            _phaseStart = now;
            _actionTime = 0;
            _liveness.SwayScale = 0f; // the clip owns the body; keep only blinking
            Logger.LogInformation("FastGlb action '{Name}' triggered.", _pendingActionName);
        }

        switch (_phase)
        {
            case ActionPhase.Idle:
                _zoom = 0f;
                _liveness.Update(_pose, now);
                break;

            case ActionPhase.ZoomOut:
            {
                double e = now - _phaseStart;
                _zoom = Smooth((float)Math.Clamp(e / ZoomDurationSec, 0, 1));
                _liveness.Update(_pose, now);
                _action.Apply(_pose, 0f); // hold the start pose while zooming out
                if (e >= ZoomDurationSec) { _phase = ActionPhase.Play; _actionTime = 0; }
                break;
            }
            case ActionPhase.Play:
                _zoom = 1f;
                _actionTime += dt;
                _liveness.Update(_pose, now);
                _action.Apply(_pose, (float)_actionTime);
                if (_actionTime >= _action.Duration) { _phase = ActionPhase.ZoomIn; _phaseStart = now; }
                break;

            case ActionPhase.ZoomIn:
            {
                double e = now - _phaseStart;
                _zoom = Smooth((float)Math.Clamp(1 - e / ZoomDurationSec, 0, 1));
                _liveness.Update(_pose, now);
                _action.Apply(_pose, _action.Duration); // hold the end pose while zooming in
                if (e >= ZoomDurationSec)
                {
                    _phase = ActionPhase.Idle;
                    _action = null;
                    _liveness.SwayScale = 1f;
                    RestoreBasePose(); // relaxed arms; drop the clip's leg/spine overrides
                }
                break;
            }
        }

        _activeCamera = CameraLerp(_faceCamera, _bodyCamera, _zoom);
    }

    private void RestoreBasePose()
    {
        _pose.NodeOverrides.Clear();
        foreach (var kv in _baseOverrides) _pose.NodeOverrides[kv.Key] = kv.Value;
    }

    private static CoreCamera CameraLerp(CoreCamera a, CoreCamera b, float t)
    {
        if (t <= 0f) return a;
        if (t >= 1f) return b;
        return new CoreCamera
        {
            Position = Vector3.Lerp(a.Position, b.Position, t),
            Target = Vector3.Lerp(a.Target, b.Target, t),
            Up = a.Up,
            FovY = a.FovY,
            Near = Math.Min(a.Near, b.Near),
            Far = Math.Max(a.Far, b.Far),
        };
    }

    private static float Smooth(float t) => t * t * (3f - 2f * t);

    private void RenderInto(AvatarModel model)
    {
        _fb.Clear(24, 26, 34, 255);
        _raster.Render(model, _activeCamera, _fb);

        var rgba = _fb.Color;
        lock (_frameLock)
        {
            int j = 0;
            for (int o = 0; o < rgba.Length; o += 4)
            {
                _bgrFrame[j++] = rgba[o + 2]; // B
                _bgrFrame[j++] = rgba[o + 1]; // G
                _bgrFrame[j++] = rgba[o + 0]; // R
            }
        }
    }

    // ---- IVideoSource ----

    public event RawVideoSampleDelegate OnVideoSourceRawSample;
    public event EncodedSampleDelegate OnVideoSourceEncodedSample;
    public event SourceErrorDelegate OnVideoSourceError;
#pragma warning disable CS0067
    public event RawVideoSampleFasterDelegate OnVideoSourceRawSampleFaster;
#pragma warning restore CS0067

    public List<VideoFormat> GetVideoSourceFormats() => _formatManager.GetSourceFormats();
    public void SetVideoSourceFormat(VideoFormat videoFormat) => _formatManager.SetSelectedFormat(videoFormat);
    public void RestrictFormats(Func<VideoFormat, bool> filter) => _formatManager.RestrictFormats(filter);
    public void ForceKeyFrame() => _videoEncoder?.ForceKeyFrame();
    public bool HasEncodedVideoSubscribers() => OnVideoSourceEncodedSample != null;
    public bool IsVideoSourcePaused() => _isPaused;

    public Task StartVideo()
    {
        if (!_isStarted)
        {
            _isStarted = true;
            _lastTickSeconds = NowSeconds();
            _renderTimer.Change(0, _frameSpacingMs);
        }
        return Task.CompletedTask;
    }

    public Task PauseVideo()
    {
        _isPaused = true;
        _renderTimer.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    public Task ResumeVideo()
    {
        _isPaused = false;
        _renderTimer.Change(0, _frameSpacingMs);
        return Task.CompletedTask;
    }

    public Task CloseVideo()
    {
        if (!_isClosed)
        {
            _isClosed = true;
            _renderTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }
        return Task.CompletedTask;
    }

    public void ExternalVideoSourceRawSample(uint durationMilliseconds, int width, int height, byte[] sample, VideoPixelFormatsEnum pixelFormat)
        => throw new NotImplementedException("FastGlbAvatarRenderer generates its own frames.");

    public void ExternalVideoSourceRawSampleFaster(uint durationMilliseconds, RawImage rawImage)
        => throw new NotImplementedException("FastGlbAvatarRenderer generates its own frames.");

    // ---- helpers ----

    private static float Total(in VisemeWeights w) => w.Aa + w.Ih + w.Ou + w.Ee + w.Oh;

    private static VisemeWeights Scale(in VisemeWeights w, float s) =>
        new() { Aa = w.Aa * s, Ih = w.Ih * s, Ou = w.Ou * s, Ee = w.Ee * s, Oh = w.Oh * s };

    // Most recently started cue covering time t (handles the provider's small overlaps).
    private static VisemeWeights MapCueAt(IReadOnlyList<VisemeCue> cues, double t)
    {
        int best = -1;
        double bestStart = double.NegativeInfinity;
        for (int i = 0; i < cues.Count; i++)
        {
            var c = cues[i];
            if (t >= c.StartSeconds && t < c.EndSeconds && c.StartSeconds >= bestStart)
            {
                best = i; bestStart = c.StartSeconds;
            }
        }
        return best < 0 ? VisemeWeights.Silent : MapVisemeName(cues[best].Name);
    }

    // Maps the demo's viseme cue names to the five VRM vowel visemes. Consonants get a
    // partial nearby shape; bilabials (pp) and silence close the mouth. Openness is
    // applied separately from audio RMS, so these are unit "shape" weights.
    private static VisemeWeights MapVisemeName(string name) => name switch
    {
        "AA" => new() { Aa = 1f },
        "IH" => new() { Ih = 1f },
        "EE" => new() { Ee = 1f },
        "OH" => new() { Oh = 1f },
        "OU" => new() { Ou = 1f },
        "ff" => new() { Ou = 0.3f },
        "TH" => new() { Ih = 0.5f },
        "CH" => new() { Ih = 0.6f },
        "ss" => new() { Ih = 0.6f },
        "dd" => new() { Ih = 0.5f },
        "nn" => new() { Ih = 0.4f },
        "kk" => new() { Aa = 0.5f },
        "rr" => new() { Ou = 0.5f },
        "pp" => VisemeWeights.Silent,
        "sil" => VisemeWeights.Silent,
        _ => new() { Aa = 0.5f },
    };

    private static int ReadFrameRate()
        => int.TryParse(Environment.GetEnvironmentVariable("AVATAR_FRAME_RATE"), out int configured)
            ? Math.Clamp(configured, 1, 30) : DefaultFrameRate;

    private static double NowSeconds() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    private static long TriangleCount(AvatarModel m)
    {
        long t = 0;
        foreach (var mesh in m.Meshes)
            foreach (var prim in mesh.Primitives) { t += prim.Indices.Length / 3; }
        return t;
    }

    // Frame the face (centroid of the mesh carrying morph targets) when present, else the whole model.
    private static CoreCamera BuildCamera(SkinnedModel skinned, AvatarModel model, float aspect)
    {
        int faceMeshIdx = skinned.Meshes.FindIndex(m => m.MorphTargetNames.Length > 0);
        if (faceMeshIdx < 0) { return CoreCamera.FrameModel(model, aspect); }

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var prim in model.Meshes[faceMeshIdx].Primitives)
            foreach (var p in prim.Positions) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
        return CoreCamera.FrameFace(model, aspect, headHeight: 0.26f, targetOverride: (min + max) * 0.5f);
    }

    public void Dispose()
    {
        CloseVideo();
        _renderTimer?.Dispose();
        _videoEncoder?.Dispose();
    }
}
