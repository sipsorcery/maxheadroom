using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Numerics;

namespace AvatarRenderer.Core.Scene;

/// <summary>
/// A fully-loaded avatar retaining everything needed to deform it per frame:
/// the node hierarchy (skeleton), skins, per-vertex skin bindings, and morph
/// targets (blendshapes). The <see cref="PoseEvaluator"/> turns this plus a
/// <see cref="Animation.Pose"/> into a flat world-space <see cref="AvatarModel"/>
/// for the rasterizer.
/// </summary>
public sealed class SkinnedModel
{
    /// <summary>All glTF nodes, in logical index order. Parent references are by index.</summary>
    public required Node[] Nodes { get; init; }

    public required List<SkinnedMesh> Meshes { get; init; }

    /// <summary>Computes every node's bind (rest) global matrix by composing the hierarchy.</summary>
    public Matrix4x4[] ComputeBindGlobals()
    {
        var global = new Matrix4x4[Nodes.Length];
        var done = new bool[Nodes.Length];
        Matrix4x4 Resolve(int i)
        {
            if (done[i]) return global[i];
            ref readonly var n = ref Nodes[i];
            global[i] = n.Parent >= 0 ? n.LocalMatrix * Resolve(n.Parent) : n.LocalMatrix;
            done[i] = true;
            return global[i];
        }
        for (int i = 0; i < global.Length; i++) Resolve(i);
        return global;
    }

    /// <summary>Finds the first node whose name contains <paramref name="substring"/> (case-insensitive), or -1.</summary>
    public int FindNode(string substring)
    {
        for (int i = 0; i < Nodes.Length; i++)
            if (Nodes[i].Name.Contains(substring, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }
}

/// <summary>A node in the hierarchy with its rest (bind) local transform.</summary>
public struct Node
{
    public int Parent;          // -1 for roots
    public string Name;
    public Vector3 Translation;
    public Quaternion Rotation;
    public Vector3 Scale;

    public readonly Matrix4x4 LocalMatrix =>
        Matrix4x4.CreateScale(Scale) *
        Matrix4x4.CreateFromQuaternion(Rotation) *
        Matrix4x4.CreateTranslation(Translation);
}

/// <summary>Skin definition: which nodes are joints, and their inverse bind matrices.</summary>
public sealed class Skin
{
    public required int[] JointNodeIndices { get; init; }
    public required Matrix4x4[] InverseBind { get; init; }
    public int JointCount => JointNodeIndices.Length;
}

public sealed class SkinnedMesh
{
    public string Name { get; init; } = "";
    /// <summary>Node this mesh is attached to (for rigid transform of non-skinned prims).</summary>
    public int NodeIndex { get; init; }
    /// <summary>Skin shared by the mesh's skinned primitives, or null if rigid.</summary>
    public Skin? Skin { get; init; }
    /// <summary>Names of this mesh's morph targets, index-aligned with primitive morph arrays.</summary>
    public string[] MorphTargetNames { get; init; } = Array.Empty<string>();
    public List<SkinnedPrimitive> Primitives { get; } = new();
}

public sealed class SkinnedPrimitive
{
    public required Vector3[] BasePositions { get; init; }
    public required Vector3[] BaseNormals { get; init; }
    public Vector2[]? TexCoords { get; init; }
    public required int[] Indices { get; init; }
    public required Material Material { get; init; }

    /// <summary>4 joint indices per vertex (into the mesh's skin), flattened. Null if rigid.</summary>
    public int[]? JointIndices { get; init; }
    /// <summary>4 skin weights per vertex, flattened, parallel to <see cref="JointIndices"/>.</summary>
    public float[]? Weights { get; init; }

    /// <summary>Morph target position/normal deltas, index-aligned with the mesh's morph names.</summary>
    public MorphTarget[] Morphs { get; init; } = Array.Empty<MorphTarget>();

    public bool IsSkinned => JointIndices is not null && Weights is not null;
    public int VertexCount => BasePositions.Length;
}

public sealed class MorphTarget
{
    public required Vector3[] PositionDeltas { get; init; }
    public Vector3[]? NormalDeltas { get; init; }
}
