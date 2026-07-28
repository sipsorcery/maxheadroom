using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Numerics;
using AvatarRenderer.Core.Scene;

namespace AvatarRenderer.Core.Animation;

/// <summary>
/// Adds procedural "liveness" to any model so it never looks frozen: periodic
/// eye blinks (via blink morph targets) and a gentle breathing sway on the
/// spine/chest/neck/head bones. Purely synthetic, so it works on models that
/// ship no idle animation of their own. Writes into a <see cref="Pose"/> each
/// frame on top of whatever lip-sync has set (different morphs/bones, no conflict).
/// </summary>
public sealed class LivenessAnimator
{
    private readonly SkinnedModel _model;
    private readonly int _spine, _chest, _neck, _head, _hips;
    private readonly string[] _blinkMorphs;

    private readonly Random _rng = new();
    private double _nextBlink = double.NaN;
    private double _blinkStart = double.NegativeInfinity;
    private const double BlinkDuration = 0.14;   // seconds for a full close+open

    public bool Enabled { get; set; } = true;
    /// <summary>Scales the sway amplitude (0 = still, 1 = default gentle motion).</summary>
    public float SwayScale { get; set; } = 1f;
    /// <summary>Random gap between blinks, seconds (min/max).</summary>
    public double BlinkIntervalMin { get; set; } = 2.0;
    public double BlinkIntervalMax { get; set; } = 6.0;

    public LivenessAnimator(SkinnedModel model)
    {
        _model = model;

        // Restrict bone lookup to actual skin joints, so we don't grab a mesh node
        // that merely happens to be named "head".
        var joints = new HashSet<int>();
        foreach (var mesh in model.Meshes)
            if (mesh.Skin is { } skin)
                foreach (var j in skin.JointNodeIndices) joints.Add(j);

        _hips = FindBone(joints, "hips", "hip");
        _spine = FindBone(joints, "spine");
        _chest = FindBone(joints, "upperchest", "upper_chest", "chest");
        _neck = FindBone(joints, "neck");
        _head = FindBone(joints, "head");
        _blinkMorphs = ResolveBlinkMorphs(model);
    }

    public bool HasBlink => _blinkMorphs.Length > 0;

    /// <summary>Updates blink + sway for absolute <paramref name="time"/> (seconds).</summary>
    public void Update(Pose pose, double time)
    {
        float s = Enabled ? SwayScale : 0f;

        // --- gentle breathing sway (small, phase-offset per bone) ---
        double t = time;
        Sway(pose, _hips, MathF.Sin((float)(t * 0.55)) * 0.25f, 0, MathF.Sin((float)(t * 0.5)) * 0.35f, s);
        Sway(pose, _spine, MathF.Sin((float)(t * 0.9)) * 0.6f, 0, MathF.Sin((float)(t * 0.6)) * 0.7f, s);
        Sway(pose, _chest, MathF.Sin((float)(t * 0.9 + 0.5)) * 0.5f, 0, MathF.Sin((float)(t * 0.6 + 0.4)) * 0.5f, s);
        Sway(pose, _neck, MathF.Sin((float)(t * 0.7)) * 0.5f, MathF.Sin((float)(t * 0.5)) * 0.7f, 0, s);
        Sway(pose, _head, MathF.Sin((float)(t * 0.65)) * 0.7f, MathF.Sin((float)(t * 0.5 + 1)) * 1.1f, 0, s);

        // --- blinking ---
        float blink = 0f;
        if (Enabled && _blinkMorphs.Length > 0)
        {
            if (double.IsNaN(_nextBlink)) _nextBlink = time + BlinkIntervalMin;
            if (time >= _nextBlink && _blinkStart < 0)
            {
                _blinkStart = time;
                _nextBlink = time + BlinkIntervalMin + _rng.NextDouble() * (BlinkIntervalMax - BlinkIntervalMin);
            }
            if (_blinkStart >= 0)
            {
                double p = (time - _blinkStart) / BlinkDuration;
                if (p >= 1.0) { blink = 0f; _blinkStart = double.NegativeInfinity; }
                else blink = p < 0.4 ? (float)(p / 0.4) : (float)(1.0 - (p - 0.4) / 0.6);
            }
        }
        foreach (var m in _blinkMorphs)
            pose.MorphWeights[m] = Math.Clamp(blink, 0f, 1f);
    }

    // Applies a small additive rotation (degrees) to a bone on top of its bind pose.
    private void Sway(Pose pose, int node, float pitchDeg, float yawDeg, float rollDeg, float scale)
    {
        if (node < 0) return;
        ref readonly var n = ref _model.Nodes[node];
        var delta = Quaternion.CreateFromYawPitchRoll(
            yawDeg * scale * MathF.PI / 180f,
            pitchDeg * scale * MathF.PI / 180f,
            rollDeg * scale * MathF.PI / 180f);
        pose.NodeOverrides[node] = new NodeTransform
        {
            Translation = n.Translation,
            Rotation = n.Rotation * delta,
            Scale = n.Scale,
        };
    }

    private int FindBone(HashSet<int> joints, params string[] substrings)
    {
        foreach (var sub in substrings)
            for (int i = 0; i < _model.Nodes.Length; i++)
                if (joints.Contains(i) && _model.Nodes[i].Name.Contains(sub, StringComparison.OrdinalIgnoreCase))
                    return i;
        return -1;
    }

    private static string[] ResolveBlinkMorphs(SkinnedModel model)
    {
        var all = new List<string>();
        foreach (var mesh in model.Meshes)
            foreach (var name in mesh.MorphTargetNames)
                all.Add(name);

        // Prefer explicit blink morphs; fall back to a full eye-close expression.
        var blink = all.Where(n => n.Contains("blink", StringComparison.OrdinalIgnoreCase)).Distinct().ToArray();
        if (blink.Length > 0) return blink;

        var eyeClose = all.Where(n =>
            n.Equals("eye_close", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("EYE_Close", StringComparison.OrdinalIgnoreCase)).Distinct().ToArray();
        return eyeClose;
    }
}
