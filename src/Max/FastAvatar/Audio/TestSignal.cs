using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
namespace AvatarRenderer.Core.Audio;

/// <summary>Generates synthetic PCM for testing lip-sync without real speech audio.</summary>
public static class TestSignal
{
    /// <summary>
    /// Writes a mono 16-bit "speech-like" WAV: alternating silence and voiced bursts
    /// at different pitches/brightness (silence→closed, dark burst→"aa", bright→"ee/ih").
    /// </summary>
    public static void WriteSpeechWav(string path, int sampleRate = 48000)
    {
        var samples = SynthesizeSpeech(sampleRate);
        WriteWav16(path, samples, sampleRate);
    }

    public static float[] SynthesizeSpeech(int sampleRate)
    {
        (double t, double d, double f, double a)[] segs =
        {
            (0.0, 0.3, 0,    0.0),
            (0.3, 0.5, 160,  0.6),   // dark, loud  -> aa
            (0.8, 0.3, 0,    0.0),
            (1.1, 0.45, 2600, 0.5),  // bright      -> ee/ih
            (1.55, 0.25, 0,  0.0),
            (1.8, 0.6, 180,  0.0),   // amplitude ramp -> varying openness
        };
        double total = 2.5;
        int n = (int)(total * sampleRate);
        var s = new float[n];

        foreach (var (t0, d, f, a) in segs)
        {
            int start = (int)(t0 * sampleRate);
            int len = (int)(d * sampleRate);
            for (int i = 0; i < len && start + i < n; i++)
            {
                if (f <= 0) continue;
                double tt = (double)i / sampleRate;
                double amp = a > 0 ? a : 0.55 * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / len));
                s[start + i] += (float)(amp * Math.Sin(2 * Math.PI * f * tt));
            }
        }
        return s;
    }

    private static void WriteWav16(string path, float[] mono, int sampleRate)
    {
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        int dataBytes = mono.Length * 2;
        bw.Write("RIFF"u8); bw.Write(36 + dataBytes); bw.Write("WAVE"u8);
        bw.Write("fmt "u8); bw.Write(16); bw.Write((short)1); bw.Write((short)1);
        bw.Write(sampleRate); bw.Write(sampleRate * 2); bw.Write((short)2); bw.Write((short)16);
        bw.Write("data"u8); bw.Write(dataBytes);
        foreach (var v in mono)
            bw.Write((short)Math.Clamp((int)(v * 32767f), short.MinValue, short.MaxValue));
    }
}
