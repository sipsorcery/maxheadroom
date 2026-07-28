using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Numerics;
using AvatarRenderer.Core.Animation;
using AvatarRenderer.Core.Scene;
using SharpGLTF.Schema2;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SceneMaterial = AvatarRenderer.Core.Scene.Material;
using SceneMesh = AvatarRenderer.Core.Scene.SkinnedMesh;
using SceneNode = AvatarRenderer.Core.Scene.Node;
using SceneSkin = AvatarRenderer.Core.Scene.Skin;

namespace AvatarRenderer.Core.Loading;

/// <summary>
/// Loads a glTF/GLB/VRM file into a <see cref="SkinnedModel"/> (skeleton, skin
/// bindings, morph targets). VRM is glTF 2.0 under the hood; VRM-specific blocks
/// (expression presets, springbones) are parsed in later phases.
/// </summary>
public static class GltfLoader
{
    public static SkinnedModel Load(string path)
    {
        var settings = new ReadSettings
        {
            Validation = SharpGLTF.Validation.ValidationMode.TryFix,
        };

        var root = ModelRoot.Load(path, settings);
        var textureCache = new Dictionary<int, Image<Rgba32>?>();

        var nodes = BuildNodes(root);
        var meshes = new List<SceneMesh>();

        foreach (var node in root.LogicalNodes)
        {
            if (node.Mesh is null) continue;

            SceneSkin? skin = node.Skin is { } gltfSkin ? BuildSkin(gltfSkin) : null;
            var morphNames = ReadMorphTargetNames(node.Mesh);

            var mesh = new SceneMesh
            {
                Name = node.Mesh.Name ?? node.Name ?? "mesh",
                NodeIndex = node.LogicalIndex,
                Skin = skin,
                MorphTargetNames = morphNames,
            };

            foreach (var prim in node.Mesh.Primitives)
            {
                if (prim.DrawPrimitiveType != PrimitiveType.TRIANGLES) continue;

                var posAccessor = prim.GetVertexAccessor("POSITION");
                if (posAccessor is null) continue;

                var positions = posAccessor.AsVector3Array().ToArray();

                Vector3[] normals = new Vector3[positions.Length];
                var normAccessor = prim.GetVertexAccessor("NORMAL");
                if (normAccessor is not null)
                {
                    var src = normAccessor.AsVector3Array();
                    for (int i = 0; i < normals.Length; i++) normals[i] = src[i];
                }

                Vector2[]? uvs = null;
                var uvAccessor = prim.GetVertexAccessor("TEXCOORD_0");
                if (uvAccessor is not null)
                {
                    var src = uvAccessor.AsVector2Array();
                    uvs = new Vector2[src.Count];
                    for (int i = 0; i < uvs.Length; i++) uvs[i] = src[i];
                }

                int[]? jointIdx = null;
                float[]? weights = null;
                if (skin is not null)
                    ReadSkinBindings(prim, positions.Length, out jointIdx, out weights);

                var indices = prim.GetIndices();
                var idx = new int[indices.Count];
                for (int i = 0; i < idx.Length; i++) idx[i] = (int)indices[i];

                var morphs = ReadMorphTargets(prim, positions.Length);
                var material = BuildMaterial(prim.Material, textureCache);

                mesh.Primitives.Add(new SkinnedPrimitive
                {
                    BasePositions = positions,
                    BaseNormals = normals,
                    TexCoords = uvs,
                    Indices = idx,
                    Material = material,
                    JointIndices = jointIdx,
                    Weights = weights,
                    Morphs = morphs,
                });
            }

            if (mesh.Primitives.Count > 0) meshes.Add(mesh);
        }

        return new SkinnedModel { Nodes = nodes, Meshes = meshes };
    }

    /// <summary>Convenience: load and evaluate at the rest (bind) pose.</summary>
    public static AvatarModel LoadBindPose(string path)
    {
        var model = Load(path);
        return new PoseEvaluator(model).Evaluate(new Pose());
    }

    private static SceneNode[] BuildNodes(ModelRoot root)
    {
        var nodes = new SceneNode[root.LogicalNodes.Count];
        foreach (var n in root.LogicalNodes)
        {
            var xf = n.LocalTransform;
            nodes[n.LogicalIndex] = new SceneNode
            {
                Parent = n.VisualParent?.LogicalIndex ?? -1,
                Name = n.Name ?? $"node{n.LogicalIndex}",
                Translation = xf.Translation,
                Rotation = xf.Rotation,
                Scale = xf.Scale,
            };
        }
        return nodes;
    }

    private static SceneSkin BuildSkin(SharpGLTF.Schema2.Skin gltfSkin)
    {
        int count = gltfSkin.JointsCount;
        var jointNodes = new int[count];
        var invBind = new Matrix4x4[count];
        for (int i = 0; i < count; i++)
        {
            var (joint, ibm) = gltfSkin.GetJoint(i);
            jointNodes[i] = joint.LogicalIndex;
            invBind[i] = ibm;
        }
        return new SceneSkin { JointNodeIndices = jointNodes, InverseBind = invBind };
    }

    private static void ReadSkinBindings(MeshPrimitive prim, int vertexCount, out int[] jointIdx, out float[] weights)
    {
        jointIdx = new int[vertexCount * 4];
        weights = new float[vertexCount * 4];

        var jAcc = prim.GetVertexAccessor("JOINTS_0");
        var wAcc = prim.GetVertexAccessor("WEIGHTS_0");
        if (jAcc is null || wAcc is null) return;

        var j = jAcc.AsVector4Array();
        var w = wAcc.AsVector4Array();
        for (int i = 0; i < vertexCount; i++)
        {
            int b = i * 4;
            var jv = j[i];
            var wv = w[i];
            jointIdx[b + 0] = (int)jv.X; jointIdx[b + 1] = (int)jv.Y;
            jointIdx[b + 2] = (int)jv.Z; jointIdx[b + 3] = (int)jv.W;

            // Normalise weights so they sum to 1 (defensive).
            float sum = wv.X + wv.Y + wv.Z + wv.W;
            if (sum > 1e-6f) wv /= sum;
            weights[b + 0] = wv.X; weights[b + 1] = wv.Y;
            weights[b + 2] = wv.Z; weights[b + 3] = wv.W;
        }
    }

    private static MorphTarget[] ReadMorphTargets(MeshPrimitive prim, int vertexCount)
    {
        int count = prim.MorphTargetsCount;
        if (count == 0) return Array.Empty<MorphTarget>();

        var targets = new MorphTarget[count];
        for (int k = 0; k < count; k++)
        {
            var accessors = prim.GetMorphTargetAccessors(k);
            var posDeltas = new Vector3[vertexCount];
            if (accessors.TryGetValue("POSITION", out var pAcc))
            {
                var src = pAcc.AsVector3Array();
                for (int i = 0; i < vertexCount && i < src.Count; i++) posDeltas[i] = src[i];
            }

            Vector3[]? nrmDeltas = null;
            if (accessors.TryGetValue("NORMAL", out var nAcc))
            {
                nrmDeltas = new Vector3[vertexCount];
                var src = nAcc.AsVector3Array();
                for (int i = 0; i < vertexCount && i < src.Count; i++) nrmDeltas[i] = src[i];
            }

            targets[k] = new MorphTarget { PositionDeltas = posDeltas, NormalDeltas = nrmDeltas };
        }
        return targets;
    }

    private static string[] ReadMorphTargetNames(SharpGLTF.Schema2.Mesh mesh)
    {
        int count = mesh.Primitives.Count > 0 ? mesh.Primitives[0].MorphTargetsCount : 0;
        var names = new string[count];
        for (int i = 0; i < count; i++) names[i] = $"morph{i}";

        // glTF stores morph names in mesh.extras.targetNames (optional).
        try
        {
            if (mesh.Extras is System.Text.Json.Nodes.JsonObject obj &&
                obj.TryGetPropertyValue("targetNames", out var node) &&
                node is System.Text.Json.Nodes.JsonArray arr)
            {
                for (int i = 0; i < count && i < arr.Count; i++)
                {
                    var s = arr[i]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(s)) names[i] = s;
                }
            }
        }
        catch { /* names stay as morphN */ }

        return names;
    }

    private static SceneMaterial BuildMaterial(
        SharpGLTF.Schema2.Material? gltfMat,
        Dictionary<int, Image<Rgba32>?> textureCache)
    {
        if (gltfMat is null) return new SceneMaterial { Name = "default" };

        var baseColor = Vector4.One;
        Image<Rgba32>? tex = null;

        var channel = gltfMat.FindChannel("BaseColor");
        if (channel is { } ch)
        {
#pragma warning disable CS0618 // Parameter is obsolete but simplest for a single Vector4.
            baseColor = new Vector4(ch.Parameter.X, ch.Parameter.Y, ch.Parameter.Z, ch.Parameter.W);
#pragma warning restore CS0618
            if (ch.Texture?.PrimaryImage is { } img)
                tex = DecodeImage(img, textureCache);
        }

        float? cutoff = gltfMat.Alpha == AlphaMode.MASK ? gltfMat.AlphaCutoff : null;

        return new SceneMaterial
        {
            Name = gltfMat.Name ?? "material",
            BaseColorFactor = baseColor,
            BaseColorTexture = tex,
            DoubleSided = gltfMat.DoubleSided,
            AlphaCutoff = cutoff,
        };
    }

    private static Image<Rgba32>? DecodeImage(SharpGLTF.Schema2.Image gltfImage, Dictionary<int, Image<Rgba32>?> cache)
    {
        int key = gltfImage.LogicalIndex;
        if (cache.TryGetValue(key, out var cached)) return cached;

        Image<Rgba32>? decoded = null;
        try
        {
            var bytes = gltfImage.Content.Content;
            if (!bytes.IsEmpty)
                decoded = SixLabors.ImageSharp.Image.Load<Rgba32>(bytes.ToArray());
        }
        catch
        {
            decoded = null;
        }

        cache[key] = decoded;
        return decoded;
    }
}
