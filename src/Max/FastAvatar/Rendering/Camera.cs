using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Numerics;
using AvatarRenderer.Core.Scene;

namespace AvatarRenderer.Core.Rendering;

/// <summary>A simple perspective camera with a right-handed look-at view.</summary>
public sealed class Camera
{
    public Vector3 Position { get; set; }
    public Vector3 Target { get; set; }
    public Vector3 Up { get; set; } = Vector3.UnitY;

    /// <summary>Vertical field of view in radians.</summary>
    public float FovY { get; set; } = MathF.PI / 4f;
    public float Near { get; set; } = 0.05f;
    public float Far { get; set; } = 100f;

    public Matrix4x4 View => Matrix4x4.CreateLookAt(Position, Target, Up);

    public Matrix4x4 Projection(float aspect) =>
        Matrix4x4.CreatePerspectiveFieldOfView(FovY, aspect, Near, Far);

    /// <summary>
    /// Positions the camera to frame the model's bounds from the front (+Z looking
    /// toward -Z, i.e. facing the avatar), fitting the vertical extent in view.
    /// </summary>
    public static Camera FrameModel(
        AvatarModel model, float aspect,
        float azimuthDeg = 0f, float elevationDeg = 0f,
        float verticalFillFraction = 0.9f)
    {
        var center = model.BoundsCenter;
        var size = model.BoundsSize;

        var cam = new Camera { Target = center };

        // Distance so the model height fills verticalFillFraction of the frame.
        float halfHeight = MathF.Max(size.Y, 0.001f) * 0.5f / verticalFillFraction;
        float halfWidth = MathF.Max(size.X, 0.001f) * 0.5f / verticalFillFraction;

        float distV = halfHeight / MathF.Tan(cam.FovY * 0.5f);
        float distH = halfWidth / (MathF.Tan(cam.FovY * 0.5f) * aspect);
        float dist = MathF.Max(distV, distH) + size.Z * 0.5f;

        // Orbit around the model. Azimuth 0 / elevation 0 views the front (+Z),
        // matching the VRM convention of avatars facing +Z.
        float az = azimuthDeg * MathF.PI / 180f;
        float el = elevationDeg * MathF.PI / 180f;
        var dir = new Vector3(
            MathF.Sin(az) * MathF.Cos(el),
            MathF.Sin(el),
            MathF.Cos(az) * MathF.Cos(el));

        cam.Position = center + dir * dist;
        cam.Near = MathF.Max(0.01f, dist - size.Length());
        cam.Far = dist + size.Length() * 2f;
        return cam;
    }

    /// <summary>
    /// Frames roughly the head — the top <paramref name="headHeight"/> metres of the
    /// model's bounds — for inspecting facial expressions / lip-sync.
    /// </summary>
    public static Camera FrameFace(
        AvatarModel model, float aspect,
        float azimuthDeg = 0f, float elevationDeg = 0f,
        float headHeight = 0.24f, float fill = 0.7f,
        Vector3? targetOverride = null)
    {
        var size = model.BoundsSize;
        var target = targetOverride ?? new Vector3(
            model.BoundsCenter.X,
            model.BoundsMax.Y - headHeight * 0.5f,
            model.BoundsCenter.Z);

        var cam = new Camera { Target = target };
        float half = headHeight * 0.5f / fill;
        float dist = half / MathF.Tan(cam.FovY * 0.5f) + size.Z * 0.5f;

        float az = azimuthDeg * MathF.PI / 180f;
        float el = elevationDeg * MathF.PI / 180f;
        var dir = new Vector3(
            MathF.Sin(az) * MathF.Cos(el),
            MathF.Sin(el),
            MathF.Cos(az) * MathF.Cos(el));

        cam.Position = target + dir * dist;
        cam.Near = 0.01f;
        cam.Far = dist + size.Length() * 2f;
        return cam;
    }
}
