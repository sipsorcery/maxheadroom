using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Numerics;
using AvatarRenderer.Core.Scene;
using SharpGLTF.Schema2;
using GltfNode = SharpGLTF.Schema2.Node;

namespace AvatarRenderer.Core.Animation;

/// <summary>
/// Retargets a <see cref="VrmaClip"/> onto a specific avatar and writes the result
/// into a <see cref="Pose"/> each frame. Bones are matched by VRM humanoid name, and
/// each bone's rotation is transferred as a world-space delta from rest (so different
/// rest-pose bone orientations between the clip's rig and the target rig are handled).
/// The hips also receive translation, scaled by the hip-height ratio.
/// </summary>
public sealed class VrmaPlayer
{
    private readonly SkinnedModel _target;
    private readonly VrmaClip _clip;

    private readonly Quaternion[] _restLocalRot;   // per target node
    private readonly Quaternion[] _restWorldRot;   // per target node

    // Parallel arrays over the bones present in both rigs, ordered parents-first.
    private readonly int[] _tNodes;
    private readonly GltfNode[] _srcNodes;
    private readonly Quaternion[] _srcRestWorld;

    private readonly int _hipsIndex = -1;          // index into the parallel arrays
    private readonly int _hipsNode = -1;
    private readonly Vector3 _tgtHipsRestLocalT;
    private readonly Vector3 _srcHipsRestLocalT;
    private readonly float _hipHeightRatio = 1f;

    // VRM 0.x avatars face -Z while VRMA clips are authored VRM-1.0 (+Z); the two differ
    // by a 180° Y rotation, which mirrors left/right. For such models we conjugate each
    // world-space delta (and the hips translation) by that rotation so the motion matches.
    private readonly bool _reversedFacing;
    private readonly Quaternion _frameCorrection = Quaternion.Identity;

    public float Duration => _clip.Duration;

    public VrmaPlayer(SkinnedModel target, Dictionary<string, int> targetBones, VrmaClip clip, bool reversedFacing = false)
    {
        _reversedFacing = reversedFacing;
        if (reversedFacing) _frameCorrection = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI);
        _target = target;
        _clip = clip;

        var globals = target.ComputeBindGlobals();
        _restLocalRot = new Quaternion[target.Nodes.Length];
        _restWorldRot = new Quaternion[target.Nodes.Length];
        for (int i = 0; i < target.Nodes.Length; i++)
        {
            _restLocalRot[i] = target.Nodes[i].Rotation;
            _restWorldRot[i] = RotOf(globals[i]);
        }

        var ordered = targetBones.Keys
            .Where(clip.Bones.ContainsKey)
            .OrderBy(b => Depth(targetBones[b]))
            .ToArray();

        _tNodes = new int[ordered.Length];
        _srcNodes = new GltfNode[ordered.Length];
        _srcRestWorld = new Quaternion[ordered.Length];
        for (int i = 0; i < ordered.Length; i++)
        {
            _tNodes[i] = targetBones[ordered[i]];
            _srcNodes[i] = clip.Root.LogicalNodes[clip.Bones[ordered[i]]];
            _srcRestWorld[i] = RotOf(_srcNodes[i].WorldMatrix);
            if (ordered[i] == "hips") _hipsIndex = i;
        }

        if (_hipsIndex >= 0)
        {
            _hipsNode = _tNodes[_hipsIndex];
            _tgtHipsRestLocalT = target.Nodes[_hipsNode].Translation;
            var srcHips = _srcNodes[_hipsIndex];
            _srcHipsRestLocalT = srcHips.LocalTransform.Translation;
            float tgtY = globals[_hipsNode].Translation.Y;
            float srcY = srcHips.WorldMatrix.Translation.Y;
            _hipHeightRatio = MathF.Abs(srcY) > 1e-4f ? tgtY / srcY : 1f;
        }
    }

    /// <summary>Applies the clip at <paramref name="time"/> seconds (looped) into the pose's node overrides.</summary>
    public void Apply(Pose pose, float time)
    {
        if (_clip.Animation is null) return;
        float t = _clip.Duration > 0 ? time % _clip.Duration : 0f;

        var animWorld = new Dictionary<int, Quaternion>(_tNodes.Length);

        for (int i = 0; i < _tNodes.Length; i++)
        {
            int tnode = _tNodes[i];
            var srcNode = _srcNodes[i];

            Quaternion sAnim = RotOf(srcNode.GetWorldMatrix(_clip.Animation, t));
            Quaternion dWorld = Quaternion.Concatenate(Quaternion.Inverse(_srcRestWorld[i]), sAnim);
            if (_reversedFacing) // dWorld' = Ry · dWorld · Ry⁻¹
                dWorld = Quaternion.Concatenate(Quaternion.Concatenate(Quaternion.Inverse(_frameCorrection), dWorld), _frameCorrection);
            Quaternion tw = Quaternion.Concatenate(_restWorldRot[tnode], dWorld);
            animWorld[tnode] = tw;

            Quaternion parentWorld = AnimatedWorld(_target.Nodes[tnode].Parent, animWorld);
            Quaternion tlocal = Quaternion.Normalize(Quaternion.Concatenate(tw, Quaternion.Inverse(parentWorld)));

            Vector3 trans = _target.Nodes[tnode].Translation;
            if (tnode == _hipsNode)
            {
                var la = srcNode.GetLocalTransform(_clip.Animation, t);
                var delta = (la.Translation - _srcHipsRestLocalT) * _hipHeightRatio;
                if (_reversedFacing) delta = Vector3.Transform(delta, _frameCorrection);
                trans = _tgtHipsRestLocalT + delta;
            }

            pose.NodeOverrides[tnode] = new NodeTransform
            {
                Translation = trans,
                Rotation = tlocal,
                Scale = _target.Nodes[tnode].Scale,
            };
        }
    }

    private Quaternion AnimatedWorld(int node, Dictionary<int, Quaternion> animWorld)
    {
        if (node < 0) return Quaternion.Identity;
        if (animWorld.TryGetValue(node, out var q)) return q;
        return Quaternion.Concatenate(_restLocalRot[node], AnimatedWorld(_target.Nodes[node].Parent, animWorld));
    }

    private int Depth(int node)
    {
        int d = 0, p = _target.Nodes[node].Parent;
        while (p >= 0) { d++; p = _target.Nodes[p].Parent; }
        return d;
    }

    private static Quaternion RotOf(Matrix4x4 m) =>
        Matrix4x4.Decompose(m, out _, out var r, out _) ? r : Quaternion.Identity;
}
