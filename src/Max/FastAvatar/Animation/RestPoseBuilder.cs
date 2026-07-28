using System;
using System.Collections.Generic;
using System.Numerics;
using AvatarRenderer.Core.Scene;

namespace AvatarRenderer.Core.Animation;

/// <summary>
/// Builds a relaxed rest pose for humanoid avatars whose bind pose is a T-pose:
/// rotates the upper-arm bones so the arms hang by the sides. It is driven by the
/// actual bone geometry (the model-space direction from the upper arm to its child
/// joint) rather than fixed local axes, so it works across rigs with different bone
/// orientations (VRM, VRoid, etc.) without needing the VRM humanoid map.
/// </summary>
public static class RestPoseBuilder
{
    /// <summary>
    /// Writes upper-arm rotation overrides into <paramref name="pose"/> so the arms rest
    /// downward. <paramref name="forward"/> and <paramref name="outward"/> add a small
    /// natural lean/splay (fractions of the down vector).
    /// </summary>
    /// <returns>Names of the bones that were relaxed.</returns>
    public static List<string> RelaxArms(SkinnedModel model, Pose pose,
        float forward = 0.10f, float outward = 0.12f)
    {
        var relaxed = new List<string>();

        // Joint set (only rotate actual skin joints, not mesh nodes).
        var joints = new HashSet<int>();
        foreach (var mesh in model.Meshes)
            if (mesh.Skin is { } skin)
                foreach (var j in skin.JointNodeIndices) joints.Add(j);

        var global = ComputeBindGlobals(model);

        for (int i = 0; i < model.Nodes.Length; i++)
        {
            if (!joints.Contains(i)) continue;
            if (!IsUpperArm(model.Nodes[i].Name)) continue;

            int child = FindArmChild(model, joints, i);
            if (child < 0) continue;

            Vector3 armPos = global[i].Translation;
            Vector3 childPos = global[child].Translation;
            Vector3 armDir = childPos - armPos;
            if (armDir.LengthSquared() < 1e-9f) continue;
            armDir = Vector3.Normalize(armDir);

            // Target: mostly down, slight forward, slight outward in the arm's own +/-X sense.
            float side = MathF.Sign(armDir.X == 0 ? 1 : armDir.X);
            Vector3 target = Vector3.Normalize(new Vector3(side * outward, -1f, forward));

            Quaternion qModel = FromTo(armDir, target);

            // Express the model-space rotation as a new local rotation for this bone:
            //   localNew = localOld · parentWorld · qModel · parentWorld⁻¹   (System.Numerics
            //   Concatenate(a,b) = "a then b").
            int parent = model.Nodes[i].Parent;
            Quaternion parentWorld = parent >= 0 ? RotationOf(global[parent]) : Quaternion.Identity;
            Quaternion localOld = model.Nodes[i].Rotation;

            Quaternion localNew = Quaternion.Concatenate(localOld, parentWorld);
            localNew = Quaternion.Concatenate(localNew, qModel);
            localNew = Quaternion.Concatenate(localNew, Quaternion.Inverse(parentWorld));

            pose.NodeOverrides[i] = new NodeTransform
            {
                Translation = model.Nodes[i].Translation,
                Rotation = Quaternion.Normalize(localNew),
                Scale = model.Nodes[i].Scale,
            };
            relaxed.Add(model.Nodes[i].Name);
        }

        return relaxed;
    }

    private static bool IsUpperArm(string name)
    {
        string n = Normalize(name);
        // "upper_arm", "UpperArm", "J_Bip_L_UpperArm" -> all normalize to contain "upperarm".
        return n.Contains("upperarm");
    }

    private static string Normalize(string s)
    {
        Span<char> buf = stackalloc char[s.Length];
        int k = 0;
        foreach (char c in s)
            if (char.IsLetterOrDigit(c)) buf[k++] = char.ToLowerInvariant(c);
        return new string(buf[..k]);
    }

    // A child joint of the upper arm (the lower arm / forearm), preferring an "arm"-named one.
    private static int FindArmChild(SkinnedModel model, HashSet<int> joints, int upperArm)
    {
        int fallback = -1;
        for (int i = 0; i < model.Nodes.Length; i++)
        {
            if (model.Nodes[i].Parent != upperArm || !joints.Contains(i)) continue;
            string n = Normalize(model.Nodes[i].Name);
            if (n.Contains("arm") || n.Contains("elbow")) return i;
            if (fallback < 0) fallback = i;
        }
        return fallback;
    }

    private static Matrix4x4[] ComputeBindGlobals(SkinnedModel model)
    {
        var global = new Matrix4x4[model.Nodes.Length];
        var done = new bool[model.Nodes.Length];
        for (int i = 0; i < global.Length; i++) Resolve(model, i, global, done);
        return global;
    }

    private static Matrix4x4 Resolve(SkinnedModel model, int i, Matrix4x4[] global, bool[] done)
    {
        if (done[i]) return global[i];
        ref readonly var node = ref model.Nodes[i];
        Matrix4x4 local = node.LocalMatrix;
        global[i] = node.Parent >= 0 ? local * Resolve(model, node.Parent, global, done) : local;
        done[i] = true;
        return global[i];
    }

    private static Quaternion RotationOf(Matrix4x4 m)
        => Matrix4x4.Decompose(m, out _, out var rot, out _) ? rot : Quaternion.Identity;

    private static Quaternion FromTo(Vector3 from, Vector3 to)
    {
        float d = Vector3.Dot(from, to);
        if (d > 0.99999f) return Quaternion.Identity;
        if (d < -0.99999f)
        {
            // 180°: rotate about any axis perpendicular to `from`.
            Vector3 axis = Vector3.Cross(Vector3.UnitX, from);
            if (axis.LengthSquared() < 1e-6f) axis = Vector3.Cross(Vector3.UnitY, from);
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.PI);
        }
        Vector3 a = Vector3.Normalize(Vector3.Cross(from, to));
        float angle = MathF.Acos(Math.Clamp(d, -1f, 1f));
        return Quaternion.CreateFromAxisAngle(a, angle);
    }
}
