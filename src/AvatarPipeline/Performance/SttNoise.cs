#nullable enable

using System;

namespace demo.Performance;

public static class SttNoise
{
    /// <summary>
    /// Adds deterministic white noise at the requested signal-to-noise ratio. The explicit
    /// xorshift generator keeps benchmark fixtures repeatable across runs and machines.
    /// </summary>
    public static short[] AdditiveWhiteNoise(short[] samples, double snrDb, int seed)
    {
        if (samples == null) throw new ArgumentNullException(nameof(samples));
        if (!double.IsFinite(snrDb) || snrDb <= 0) throw new ArgumentOutOfRangeException(nameof(snrDb));
        if (seed == 0) throw new ArgumentOutOfRangeException(nameof(seed), "The noise seed must be non-zero.");
        if (samples.Length == 0) return Array.Empty<short>();

        double signalSquared = 0;
        foreach (var sample in samples)
        {
            signalSquared += (double)sample * sample;
        }
        var signalRms = Math.Sqrt(signalSquared / samples.Length);
        if (signalRms == 0) return (short[])samples.Clone();

        var noiseRms = signalRms / Math.Pow(10, snrDb / 20d);
        var result = new short[samples.Length];
        uint state = unchecked((uint)seed);
        for (var i = 0; i < samples.Length; i++)
        {
            var u1 = NextUnit(ref state);
            var u2 = NextUnit(ref state);
            var gaussian = Math.Sqrt(-2d * Math.Log(u1)) * Math.Cos(2d * Math.PI * u2);
            var value = samples[i] + gaussian * noiseRms;
            result[i] = (short)Math.Clamp(Math.Round(value), short.MinValue, short.MaxValue);
        }
        return result;
    }

    private static double NextUnit(ref uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return (state + 1d) / (uint.MaxValue + 2d);
    }
}
