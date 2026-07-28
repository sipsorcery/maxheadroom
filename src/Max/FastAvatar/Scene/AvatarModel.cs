using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Numerics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace AvatarRenderer.Core.Scene;

/// <summary>
/// A renderer-agnostic representation of a loaded avatar. Geometry is already
/// baked into model space (bind pose) for Phase 1; skinning is layered on later.
/// </summary>
public sealed class AvatarModel
{
    public List<Mesh> Meshes { get; } = new();

    /// <summary>Axis-aligned bounds of all geometry, used for auto-framing the camera.</summary>
    public Vector3 BoundsMin { get; set; } = new(float.MaxValue);
    public Vector3 BoundsMax { get; set; } = new(float.MinValue);

    public Vector3 BoundsCenter => (BoundsMin + BoundsMax) * 0.5f;
    public Vector3 BoundsSize => BoundsMax - BoundsMin;

    public void GrowBounds(Vector3 p)
    {
        BoundsMin = Vector3.Min(BoundsMin, p);
        BoundsMax = Vector3.Max(BoundsMax, p);
    }
}

public sealed class Mesh
{
    public string Name { get; init; } = "";
    public List<Primitive> Primitives { get; } = new();
}

/// <summary>A single draw-call worth of triangles sharing one material.</summary>
public sealed class Primitive
{
    public required Vector3[] Positions { get; init; }
    public required Vector3[] Normals { get; init; }
    public Vector2[]? TexCoords { get; init; }
    public required int[] Indices { get; init; }
    public required Material Material { get; init; }
}

public sealed class Material
{
    public string Name { get; init; } = "";
    public Vector4 BaseColorFactor { get; init; } = Vector4.One;
    public Image<Rgba32>? BaseColorTexture { get; init; }
    public bool DoubleSided { get; init; }
    /// <summary>Alpha cutoff for MASK mode; null means blend/opaque.</summary>
    public float? AlphaCutoff { get; init; }
}
