using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using AvatarRenderer.Core.Animation;
using AvatarRenderer.Core.Scene;

namespace AvatarRenderer.Core.LipSync;

/// <summary>
/// Resolves the five abstract visemes to concrete morph-target names present on a
/// given model, so lip-sync works across VRMs that name their mouth blendshapes
/// differently (lip_a, A, Fcl_MTH_A, vrc.v_aa, …). This is a heuristic name match;
/// a later refinement can source the mapping from parsed VRM expression presets.
/// </summary>
public sealed class VisemeMapper
{
    // Candidate morph names per viseme, highest priority first. Matched case-insensitively by equality.
    private static readonly Dictionary<Viseme, string[]> Candidates = new()
    {
        [Viseme.Aa] = new[] { "aa", "a", "lip_a", "mouth_a", "fcl_mth_a", "vrc.v_aa" },
        [Viseme.Ih] = new[] { "ih", "i", "lip_i", "mouth_i", "fcl_mth_i", "vrc.v_ih" },
        [Viseme.Ou] = new[] { "ou", "u", "lip_u", "mouth_u", "fcl_mth_u", "vrc.v_ou" },
        [Viseme.Ee] = new[] { "ee", "e", "lip_e", "mouth_e", "fcl_mth_e", "vrc.v_ee" },
        [Viseme.Oh] = new[] { "oh", "o", "lip_o", "mouth_o", "fcl_mth_o", "vrc.v_oh" },
    };

    private readonly Dictionary<Viseme, string> _resolved = new();

    public VisemeMapper(SkinnedModel model)
    {
        var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mesh in model.Meshes)
            foreach (var name in mesh.MorphTargetNames)
                available.Add(name);

        foreach (var (viseme, cands) in Candidates)
            foreach (var c in cands)
                if (available.Contains(c) && FindActualName(model, c) is { } actual)
                {
                    _resolved[viseme] = actual;
                    break;
                }
    }

    /// <summary>Visemes that were successfully mapped to a morph on this model.</summary>
    public IReadOnlyDictionary<Viseme, string> Resolved => _resolved;

    public bool IsUsable => _resolved.Count > 0;

    /// <summary>Writes viseme weights into a pose as morph-target weights.</summary>
    public void Apply(Pose pose, in VisemeWeights w)
    {
        foreach (var (viseme, morph) in _resolved)
            pose.MorphWeights[morph] = w[viseme];
    }

    // Recovers the original-cased morph name (pose lookups are Ordinal, so case must match the model's).
    private static string? FindActualName(SkinnedModel model, string candidate)
    {
        foreach (var mesh in model.Meshes)
            foreach (var name in mesh.MorphTargetNames)
                if (string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase))
                    return name;
        return null;
    }
}
