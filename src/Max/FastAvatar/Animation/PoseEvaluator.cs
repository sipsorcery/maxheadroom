using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Numerics;
using AvatarRenderer.Core.Scene;

namespace AvatarRenderer.Core.Animation;

/// <summary>
/// Turns a <see cref="SkinnedModel"/> + <see cref="Pose"/> into flat world-space
/// geometry (an <see cref="AvatarModel"/>) that the rasterizer consumes. The
/// render model is allocated once and its vertex arrays are overwritten in place
/// each frame, so per-frame evaluation does no allocation.
/// </summary>
public sealed class PoseEvaluator
{
    private readonly SkinnedModel _model;
    private readonly AvatarModel _render = new();

    // Scratch for node global matrices, recomputed per Evaluate.
    private readonly Matrix4x4[] _global;
    private readonly bool[] _computed;
    // Reused per-frame skinning-matrix buffer, sized to the largest skin.
    private readonly Matrix4x4[] _skinMats;

    // Maps render primitives back to their source skinned primitives (same order).
    private readonly List<(SkinnedMesh mesh, SkinnedPrimitive src, Primitive dst)> _map = new();

    public PoseEvaluator(SkinnedModel model)
    {
        _model = model;
        _global = new Matrix4x4[model.Nodes.Length];
        _computed = new bool[model.Nodes.Length];
        int maxJoints = 0;
        foreach (var m in model.Meshes)
            if (m.Skin is { } s) maxJoints = Math.Max(maxJoints, s.JointCount);
        _skinMats = new Matrix4x4[Math.Max(1, maxJoints)];
        BuildRenderModel();
    }

    public AvatarModel RenderModel => _render;

    /// <summary>World-space position of a node after the most recent <see cref="Evaluate"/> (excludes ModelTransform).</summary>
    public Vector3 NodeWorldPosition(int nodeIndex) => _global[nodeIndex].Translation;

    private void BuildRenderModel()
    {
        foreach (var mesh in _model.Meshes)
        {
            var rMesh = new Mesh { Name = mesh.Name };
            foreach (var src in mesh.Primitives)
            {
                var dst = new Primitive
                {
                    Positions = new Vector3[src.VertexCount],
                    Normals = new Vector3[src.VertexCount],
                    TexCoords = src.TexCoords, // uv is static, share it
                    Indices = src.Indices,     // topology is static, share it
                    Material = src.Material,
                };
                rMesh.Primitives.Add(dst);
                _map.Add((mesh, src, dst));
            }
            _render.Meshes.Add(rMesh);
        }
    }

    public AvatarModel Evaluate(Pose pose)
    {
        ComputeGlobals(pose);

        _render.BoundsMin = new Vector3(float.MaxValue);
        _render.BoundsMax = new Vector3(float.MinValue);

        foreach (var (mesh, src, dst) in _map)
        {
            if (src.IsSkinned && mesh.Skin is { } skin)
                EvaluateSkinned(mesh, skin, src, dst, pose);
            else
                EvaluateRigid(mesh, src, dst, pose);
        }

        return _render;
    }

    private void ComputeGlobals(Pose pose)
    {
        Array.Clear(_computed);
        for (int i = 0; i < _global.Length; i++)
            ResolveGlobal(i, pose);
    }

    private Matrix4x4 ResolveGlobal(int nodeIndex, Pose pose)
    {
        if (_computed[nodeIndex]) return _global[nodeIndex];

        ref readonly var node = ref _model.Nodes[nodeIndex];
        Matrix4x4 local = pose.NodeOverrides.TryGetValue(nodeIndex, out var ov)
            ? ov.Matrix
            : node.LocalMatrix;

        Matrix4x4 global = node.Parent >= 0
            ? local * ResolveGlobal(node.Parent, pose)
            : local;

        _global[nodeIndex] = global;
        _computed[nodeIndex] = true;
        return global;
    }

    private void EvaluateSkinned(SkinnedMesh mesh, Skin skin, SkinnedPrimitive src, Primitive dst, Pose pose)
    {
        // Precompute skinning matrices: invBind * globalJoint (row-vector convention).
        int jc = skin.JointCount;
        var skinMats = _skinMats;
        for (int j = 0; j < jc; j++)
            skinMats[j] = skin.InverseBind[j] * _global[skin.JointNodeIndices[j]];

        // Resolve this mesh's active morph weights once.
        var morphWeights = ResolveMorphWeights(mesh, pose);

        var joints = src.JointIndices!;
        var weights = src.Weights!;
        var basePos = src.BasePositions;
        var baseNrm = src.BaseNormals;

        for (int v = 0; v < src.VertexCount; v++)
        {
            Vector3 p = basePos[v];
            Vector3 n = baseNrm[v];
            ApplyMorphs(src, morphWeights, v, ref p, ref n);

            int b = v * 4;
            Vector3 sp = Vector3.Zero;
            Vector3 sn = Vector3.Zero;
            for (int k = 0; k < 4; k++)
            {
                float w = weights[b + k];
                if (w == 0f) continue;
                var m = skinMats[joints[b + k]];
                sp += Vector3.Transform(p, m) * w;
                sn += Vector3.TransformNormal(n, m) * w;
            }

            var wp = Vector3.Transform(sp, pose.ModelTransform);
            dst.Positions[v] = wp;
            dst.Normals[v] = Vector3.Normalize(Vector3.TransformNormal(sn, pose.ModelTransform));
            _render.GrowBounds(wp);
        }
    }

    private void EvaluateRigid(SkinnedMesh mesh, SkinnedPrimitive src, Primitive dst, Pose pose)
    {
        Matrix4x4 world = _global[mesh.NodeIndex] * pose.ModelTransform;
        var morphWeights = ResolveMorphWeights(mesh, pose);

        for (int v = 0; v < src.VertexCount; v++)
        {
            Vector3 p = src.BasePositions[v];
            Vector3 n = src.BaseNormals[v];
            ApplyMorphs(src, morphWeights, v, ref p, ref n);

            var wp = Vector3.Transform(p, world);
            dst.Positions[v] = wp;
            dst.Normals[v] = Vector3.Normalize(Vector3.TransformNormal(n, world));
            _render.GrowBounds(wp);
        }
    }

    private static void ApplyMorphs(SkinnedPrimitive src, (int idx, float w)[] morphWeights, int v, ref Vector3 p, ref Vector3 n)
    {
        foreach (var (idx, w) in morphWeights)
        {
            var mt = src.Morphs[idx];
            p += mt.PositionDeltas[v] * w;
            if (mt.NormalDeltas is { } nd) n += nd[v] * w;
        }
    }

    // Resolve active (non-zero) morph weights for a mesh into index/weight pairs.
    private (int idx, float w)[] ResolveMorphWeights(SkinnedMesh mesh, Pose pose)
    {
        if (pose.MorphWeights.Count == 0 || mesh.MorphTargetNames.Length == 0)
            return Array.Empty<(int, float)>();

        var list = new List<(int, float)>();
        for (int i = 0; i < mesh.MorphTargetNames.Length; i++)
        {
            if (pose.MorphWeights.TryGetValue(mesh.MorphTargetNames[i], out float w) && w != 0f)
                list.Add((i, w));
        }
        return list.Count == 0 ? Array.Empty<(int, float)>() : list.ToArray();
    }
}
