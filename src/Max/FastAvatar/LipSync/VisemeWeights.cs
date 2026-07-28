using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
namespace AvatarRenderer.Core.LipSync;

/// <summary>The five standard VRM mouth visemes (VRM 1.0 expression presets).</summary>
public enum Viseme { Aa, Ih, Ou, Ee, Oh }

/// <summary>
/// Instantaneous weights (0..1) for the five VRM visemes. Alloc-free value type so
/// the per-frame lip-sync path does no garbage.
/// </summary>
public struct VisemeWeights
{
    public float Aa, Ih, Ou, Ee, Oh;

    public static readonly VisemeWeights Silent = default;

    public float this[Viseme v]
    {
        readonly get => v switch
        {
            Viseme.Aa => Aa, Viseme.Ih => Ih, Viseme.Ou => Ou,
            Viseme.Ee => Ee, Viseme.Oh => Oh, _ => 0f,
        };
        set
        {
            switch (v)
            {
                case Viseme.Aa: Aa = value; break;
                case Viseme.Ih: Ih = value; break;
                case Viseme.Ou: Ou = value; break;
                case Viseme.Ee: Ee = value; break;
                case Viseme.Oh: Oh = value; break;
            }
        }
    }

    /// <summary>Linear interpolation toward <paramref name="target"/> by <paramref name="t"/> (for attack/release smoothing).</summary>
    public readonly VisemeWeights LerpTo(in VisemeWeights target, float t) => new()
    {
        Aa = Aa + (target.Aa - Aa) * t,
        Ih = Ih + (target.Ih - Ih) * t,
        Ou = Ou + (target.Ou - Ou) * t,
        Ee = Ee + (target.Ee - Ee) * t,
        Oh = Oh + (target.Oh - Oh) * t,
    };
}
