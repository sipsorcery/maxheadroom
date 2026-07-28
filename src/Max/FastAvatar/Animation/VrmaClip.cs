using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using AvatarRenderer.Core.Loading;
using SharpGLTF.Schema2;
using GltfAnimation = SharpGLTF.Schema2.Animation;

namespace AvatarRenderer.Core.Animation;

/// <summary>
/// A loaded VRM Animation (.vrma) clip. VRMA is a glTF file whose animation targets
/// a humanoid skeleton described by the <c>VRMC_vrm_animation</c> extension. This holds
/// the SharpGLTF model (used to evaluate the animation at a time) plus the humanoid
/// bone → source-node map; retargeting to a specific avatar is done by <see cref="VrmaPlayer"/>.
/// </summary>
public sealed class VrmaClip
{
    public ModelRoot Root { get; }
    public GltfAnimation? Animation { get; }
    /// <summary>VRM humanoid bone name → source node index.</summary>
    public IReadOnlyDictionary<string, int> Bones { get; }
    public float Duration { get; }

    public VrmaClip(string path)
    {
        Root = ModelRoot.Load(path, new ReadSettings { Validation = SharpGLTF.Validation.ValidationMode.TryFix });
        Animation = Root.LogicalAnimations.Count > 0 ? Root.LogicalAnimations[0] : null;
        Bones = VrmHumanoid.Parse(path);
        Duration = Animation?.Duration ?? 0f;
    }
}
