using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Numerics;

namespace AvatarRenderer.Core.Animation;

/// <summary>
/// Describes how to deform a <see cref="Scene.SkinnedModel"/> for one frame:
/// per-node local-transform overrides (bone posing) and morph-target weights
/// (blendshapes / expressions), plus an overall model transform.
/// </summary>
public sealed class Pose
{
    /// <summary>Overrides a node's local transform (e.g. rotate a shoulder). Keyed by node index.</summary>
    public Dictionary<int, NodeTransform> NodeOverrides { get; } = new();

    /// <summary>Morph-target weights by target name (0..1), applied to any mesh with a matching target.</summary>
    public Dictionary<string, float> MorphWeights { get; } = new(StringComparer.Ordinal);

    /// <summary>Applied to all geometry after skinning (e.g. root rotation to face the camera).</summary>
    public Matrix4x4 ModelTransform { get; set; } = Matrix4x4.Identity;

    public void SetMorph(string name, float weight) => MorphWeights[name] = weight;

    public void RotateNode(int nodeIndex, Quaternion rotation, NodeTransform bind)
    {
        var t = NodeOverrides.TryGetValue(nodeIndex, out var existing) ? existing : bind;
        t.Rotation = rotation;
        NodeOverrides[nodeIndex] = t;
    }
}

public struct NodeTransform
{
    public Vector3 Translation;
    public Quaternion Rotation;
    public Vector3 Scale;

    public readonly Matrix4x4 Matrix =>
        Matrix4x4.CreateScale(Scale) *
        Matrix4x4.CreateFromQuaternion(Rotation) *
        Matrix4x4.CreateTranslation(Translation);
}
