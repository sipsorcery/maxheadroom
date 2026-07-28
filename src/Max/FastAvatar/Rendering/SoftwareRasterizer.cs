using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Numerics;
using AvatarRenderer.Core.Scene;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace AvatarRenderer.Core.Rendering;

/// <summary>
/// A CPU triangle rasterizer with a z-buffer, perspective-correct interpolation,
/// simple directional + ambient lambert shading, nearest-neighbour texturing and
/// alpha-mask cutout. No GPU, no native dependencies.
///
/// Rasterization is parallelized by splitting the framebuffer into horizontal
/// row-bands: triangles are projected once, then each band (on its own core)
/// rasterizes every triangle but only touches its own rows, so colour/depth writes
/// never race.
/// </summary>
public sealed class SoftwareRasterizer : IRenderer
{
    private static readonly Vector3 LightDir = Vector3.Normalize(new Vector3(-0.3f, -0.4f, -1.0f));
    private const float Ambient = 0.35f;
    private const float DiffuseStrength = 0.85f;

    public bool Parallel { get; set; } = true;
    /// <summary>Cull back-facing triangles of single-sided materials (double-sided ones always draw).</summary>
    public bool BackfaceCull { get; set; } = true;
    /// <summary>Triangles culled in the last frame (diagnostic).</summary>
    public int LastCulled { get; private set; }

    private const int BandRows = 8; // framebuffer rows per rasterization band

    private PreparedTri[] _tris = new PreparedTri[1024];
    private int _triCount;
    private readonly List<Material> _materials = new();

    // Per-band bins of triangle indices, so each band only visits triangles that overlap it.
    private List<int>[] _bands = Array.Empty<List<int>>();
    private int _bandCount;
    private Framebuffer? _fb;

    private struct PreparedTri
    {
        public Vector3 S0, S1, S2;     // screen x,y + ndc z
        public float InvW0, InvW1, InvW2;
        public Vector3 N0, N1, N2;     // world normals
        public Vector2 U0, U1, U2;     // uv
        public bool HasUv;
        public float InvArea;
        public int MatId;
        public int MinX, MinY, MaxX, MaxY;
    }

    public void Render(AvatarModel model, Camera camera, Framebuffer target)
    {
        float aspect = (float)target.Width / target.Height;
        var viewProj = camera.View * camera.Projection(aspect);

        _fb = target;
        SetupBands(target.Height);
        Prepare(model, viewProj, target);

        if (Parallel && _bandCount > 1)
            System.Threading.Tasks.Parallel.For(0, _bandCount, RenderBandBinned);
        else
            for (int b = 0; b < _bandCount; b++) RenderBandBinned(b);
    }

    private void SetupBands(int height)
    {
        _bandCount = (height + BandRows - 1) / BandRows;
        if (_bands.Length < _bandCount)
        {
            var grown = new List<int>[_bandCount];
            Array.Copy(_bands, grown, _bands.Length);
            for (int i = _bands.Length; i < _bandCount; i++) grown[i] = new List<int>();
            _bands = grown;
        }
        for (int i = 0; i < _bandCount; i++) _bands[i].Clear();
    }

    // ---- Pass 1: project every triangle into screen space (serial) --------------

    private void Prepare(AvatarModel model, Matrix4x4 viewProj, Framebuffer fb)
    {
        _triCount = 0;
        _materials.Clear();
        LastCulled = 0;

        foreach (var mesh in model.Meshes)
            foreach (var prim in mesh.Primitives)
            {
                int matId = _materials.Count;
                _materials.Add(prim.Material);
                bool cull = BackfaceCull && !prim.Material.DoubleSided;
                PreparePrimitive(prim, matId, cull, viewProj, fb);
            }
    }

    private void PreparePrimitive(Primitive prim, int matId, bool cull, Matrix4x4 viewProj, Framebuffer fb)
    {
        var pos = prim.Positions;
        var nrm = prim.Normals;
        var uv = prim.TexCoords;
        var idx = prim.Indices;
        bool hasUv = uv is not null;

        for (int t = 0; t + 2 < idx.Length; t += 3)
        {
            int i0 = idx[t], i1 = idx[t + 1], i2 = idx[t + 2];

            var c0 = Vector4.Transform(new Vector4(pos[i0], 1f), viewProj);
            var c1 = Vector4.Transform(new Vector4(pos[i1], 1f), viewProj);
            var c2 = Vector4.Transform(new Vector4(pos[i2], 1f), viewProj);
            if (c0.W <= 0f || c1.W <= 0f || c2.W <= 0f) continue;

            float invW0 = 1f / c0.W, invW1 = 1f / c1.W, invW2 = 1f / c2.W;
            var s0 = ToScreen(new Vector3(c0.X, c0.Y, c0.Z) * invW0, fb);
            var s1 = ToScreen(new Vector3(c1.X, c1.Y, c1.Z) * invW1, fb);
            var s2 = ToScreen(new Vector3(c2.X, c2.Y, c2.Z) * invW2, fb);

            float area = Edge(s0, s1, s2);
            if (MathF.Abs(area) < 1e-7f) continue;

            // Back faces (single-sided) have positive screen-space area after the Y flip.
            if (cull && area > 0f) { LastCulled++; continue; }

            int minX = Math.Max(0, (int)MathF.Floor(Min3(s0.X, s1.X, s2.X)));
            int maxX = Math.Min(fb.Width - 1, (int)MathF.Ceiling(Max3(s0.X, s1.X, s2.X)));
            int minY = Math.Max(0, (int)MathF.Floor(Min3(s0.Y, s1.Y, s2.Y)));
            int maxY = Math.Min(fb.Height - 1, (int)MathF.Ceiling(Max3(s0.Y, s1.Y, s2.Y)));
            if (minX > maxX || minY > maxY) continue;

            Vector3 faceN = Vector3.Normalize(Vector3.Cross(pos[i1] - pos[i0], pos[i2] - pos[i0]));
            Vector3 n0 = nrm[i0].LengthSquared() > 1e-8f ? nrm[i0] : faceN;
            Vector3 n1 = nrm[i1].LengthSquared() > 1e-8f ? nrm[i1] : faceN;
            Vector3 n2 = nrm[i2].LengthSquared() > 1e-8f ? nrm[i2] : faceN;

            if (_triCount >= _tris.Length) Array.Resize(ref _tris, _tris.Length * 2);
            int ti = _triCount++;
            _tris[ti] = new PreparedTri
            {
                S0 = s0, S1 = s1, S2 = s2,
                InvW0 = invW0, InvW1 = invW1, InvW2 = invW2,
                N0 = n0, N1 = n1, N2 = n2,
                U0 = hasUv ? uv![i0] : default,
                U1 = hasUv ? uv![i1] : default,
                U2 = hasUv ? uv![i2] : default,
                HasUv = hasUv,
                InvArea = 1f / area,
                MatId = matId,
                MinX = minX, MinY = minY, MaxX = maxX, MaxY = maxY,
            };

            // Bin into every band this triangle's vertical extent overlaps.
            int b0 = minY / BandRows, b1 = maxY / BandRows;
            for (int b = b0; b <= b1; b++) _bands[b].Add(ti);
        }
    }

    // ---- Pass 2: rasterize each band from its triangle bin ----------------------

    private void RenderBandBinned(int band)
    {
        var fb = _fb!;
        int rowStart = band * BandRows;
        int rowEnd = Math.Min(fb.Height, rowStart + BandRows);
        var bin = _bands[band];
        var tris = _tris;

        for (int k = 0; k < bin.Count; k++)
        {
            ref readonly var tr = ref tris[bin[k]];

            var mat = _materials[tr.MatId];
            var tex = mat.BaseColorTexture;
            var baseFactor = mat.BaseColorFactor;
            float? cutoff = mat.AlphaCutoff;

            int y0 = Math.Max(rowStart, tr.MinY);
            int y1 = Math.Min(rowEnd - 1, tr.MaxY);

            // Perspective-correct barycentric weights are linear in screen space, so we
            // compute them at the row start and step by a constant per pixel (3 adds),
            // instead of re-evaluating three edge functions (6 muls) at every pixel.
            float ia = tr.InvArea;
            float sw0 = -(tr.S2.Y - tr.S1.Y) * ia; // d(w0)/dx
            float sw1 = -(tr.S0.Y - tr.S2.Y) * ia;
            float sw2 = -(tr.S1.Y - tr.S0.Y) * ia;

            for (int y = y0; y <= y1; y++)
            {
                int rowBase = y * fb.Width;
                float cx = tr.MinX + 0.5f, cy = y + 0.5f;
                float w0 = ((tr.S2.X - tr.S1.X) * (cy - tr.S1.Y) - (tr.S2.Y - tr.S1.Y) * (cx - tr.S1.X)) * ia;
                float w1 = ((tr.S0.X - tr.S2.X) * (cy - tr.S2.Y) - (tr.S0.Y - tr.S2.Y) * (cx - tr.S2.X)) * ia;
                float w2 = ((tr.S1.X - tr.S0.X) * (cy - tr.S0.Y) - (tr.S1.Y - tr.S0.Y) * (cx - tr.S0.X)) * ia;

                for (int x = tr.MinX; x <= tr.MaxX; x++, w0 += sw0, w1 += sw1, w2 += sw2)
                {
                    if (w0 < 0f || w1 < 0f || w2 < 0f) continue;

                    float depth = w0 * tr.S0.Z + w1 * tr.S1.Z + w2 * tr.S2.Z;
                    int pi = rowBase + x;
                    if (depth >= fb.Depth[pi]) continue;

                    float pw0 = w0 * tr.InvW0, pw1 = w1 * tr.InvW1, pw2 = w2 * tr.InvW2;
                    float invSum = 1f / (pw0 + pw1 + pw2);
                    pw0 *= invSum; pw1 *= invSum; pw2 *= invSum;

                    Vector4 color = baseFactor;
                    if (tex is not null && tr.HasUv)
                    {
                        var uvp = pw0 * tr.U0 + pw1 * tr.U1 + pw2 * tr.U2;
                        color *= SampleNearest(tex, uvp);
                    }
                    if (cutoff is { } cut && color.W < cut) continue;

                    Vector3 n = Vector3.Normalize(pw0 * tr.N0 + pw1 * tr.N1 + pw2 * tr.N2);
                    float ndl = MathF.Abs(Vector3.Dot(n, -LightDir));
                    float lit = Ambient + DiffuseStrength * Math.Clamp(ndl, 0f, 1f);

                    fb.Depth[pi] = depth;
                    int co = pi * 4;
                    fb.Color[co + 0] = ToByte(color.X * lit);
                    fb.Color[co + 1] = ToByte(color.Y * lit);
                    fb.Color[co + 2] = ToByte(color.Z * lit);
                    fb.Color[co + 3] = 255;
                }
            }
        }
    }

    private static Vector3 ToScreen(Vector3 ndc, Framebuffer fb) => new(
        (ndc.X * 0.5f + 0.5f) * fb.Width,
        (1f - (ndc.Y * 0.5f + 0.5f)) * fb.Height,
        ndc.Z);

    private static float Edge(Vector3 a, Vector3 b, Vector3 c) =>
        (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    private static Vector4 SampleNearest(Image<Rgba32> tex, Vector2 uv)
    {
        float u = uv.X - MathF.Floor(uv.X);
        float v = uv.Y - MathF.Floor(uv.Y);
        int px = Math.Clamp((int)(u * tex.Width), 0, tex.Width - 1);
        int py = Math.Clamp((int)(v * tex.Height), 0, tex.Height - 1);
        var c = tex[px, py];
        return new Vector4(c.R, c.G, c.B, c.A) / 255f;
    }

    private static byte ToByte(float v) => (byte)Math.Clamp(v * 255f + 0.5f, 0f, 255f);
    private static float Min3(float a, float b, float c) => MathF.Min(a, MathF.Min(b, c));
    private static float Max3(float a, float b, float c) => MathF.Max(a, MathF.Max(b, c));
}
