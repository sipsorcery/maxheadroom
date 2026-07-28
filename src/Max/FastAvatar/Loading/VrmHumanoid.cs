using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text.Json;

namespace AvatarRenderer.Core.Loading;

/// <summary>
/// Parses the VRM humanoid bone → glTF-node mapping from a .vrm / .glb / .vrma file
/// by reading the container's JSON chunk directly. Supports VRM 1.0
/// (<c>VRMC_vrm.humanoid</c>), VRM 0.x (<c>VRM.humanoid</c>) and VRM Animation
/// (<c>VRMC_vrm_animation.humanoid</c>). Bone names are the VRM canonical names
/// (hips, spine, leftUpperArm, …); keys are compared case-insensitively.
/// </summary>
public static class VrmHumanoid
{
    /// <summary>Maps VRM humanoid bone name → node index. Empty if the file has no humanoid map.</summary>
    public static Dictionary<string, int> Parse(string path)
    {
        var json = ReadJsonChunk(path);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        if (!root.TryGetProperty("extensions", out var ext)) return map;

        // VRM 1.0 and VRM Animation share the same { humanBones: { bone: { node } } } shape.
        foreach (var extName in new[] { "VRMC_vrm", "VRMC_vrm_animation" })
        {
            if (ext.TryGetProperty(extName, out var vrmExt) &&
                vrmExt.TryGetProperty("humanoid", out var humanoid) &&
                humanoid.TryGetProperty("humanBones", out var bones) &&
                bones.ValueKind == JsonValueKind.Object)
            {
                foreach (var bone in bones.EnumerateObject())
                    if (bone.Value.TryGetProperty("node", out var node) && node.TryGetInt32(out int idx))
                        map[bone.Name] = idx;
                if (map.Count > 0) return map;
            }
        }

        // VRM 0.x: humanBones is an array of { bone, node }.
        if (ext.TryGetProperty("VRM", out var vrm0) &&
            vrm0.TryGetProperty("humanoid", out var h0) &&
            h0.TryGetProperty("humanBones", out var arr) &&
            arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var b in arr.EnumerateArray())
                if (b.TryGetProperty("bone", out var name) && b.TryGetProperty("node", out var node) &&
                    node.TryGetInt32(out int idx))
                    map[name.GetString() ?? ""] = idx;
        }

        return map;
    }

    // Returns the glTF JSON: the JSON chunk of a GLB, or the whole file for a .gltf.
    private static byte[] ReadJsonChunk(string path)
    {
        var bytes = File.ReadAllBytes(path);
        // GLB magic "glTF" = 0x46546C67 little-endian.
        if (bytes.Length >= 12 && bytes[0] == 0x67 && bytes[1] == 0x6C && bytes[2] == 0x54 && bytes[3] == 0x46)
        {
            // Header: magic(4) version(4) length(4). Then chunks: length(4) type(4) data.
            int chunkLen = BitConverter.ToInt32(bytes, 12);
            // chunk type at 16..20 should be "JSON" (0x4E4F534A).
            int dataStart = 20;
            return bytes.AsSpan(dataStart, chunkLen).ToArray();
        }
        return bytes; // assume text .gltf
    }
}
