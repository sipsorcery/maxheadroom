using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
namespace AvatarRenderer.Core.Audio;

/// <summary>
/// Reads a PCM WAV file (8/16/24/32-bit int or 32-bit float) and exposes it as
/// mono float samples. Multi-channel input is downmixed to mono by averaging.
/// Enough for driving lip-sync; not a general-purpose audio decoder.
/// </summary>
public sealed class WavAudioSource : IAudioSource
{
    private readonly float[] _samples; // mono
    private int _pos;

    public int SampleRate { get; }
    /// <summary>When true, playback wraps to the start instead of ending (for continuous perf runs).</summary>
    public bool Loop { get; set; }
    public bool IsComplete => !Loop && _pos >= _samples.Length;
    public int TotalSamples => _samples.Length;
    public double DurationSeconds => (double)_samples.Length / SampleRate;

    public void Rewind() => _pos = 0;

    public WavAudioSource(string path)
    {
        var bytes = File.ReadAllBytes(path);
        (_samples, SampleRate) = Decode(bytes);
    }

    public int Read(Span<float> buffer)
    {
        if (_samples.Length == 0) return 0;
        int total = 0;
        while (total < buffer.Length)
        {
            int n = Math.Min(buffer.Length - total, _samples.Length - _pos);
            if (n <= 0)
            {
                if (Loop) { _pos = 0; continue; }
                break;
            }
            _samples.AsSpan(_pos, n).CopyTo(buffer.Slice(total, n));
            _pos += n;
            total += n;
        }
        return total;
    }

    /// <summary>Reads a window centred/anchored at an absolute sample index without advancing state.</summary>
    public int ReadAt(int startSample, Span<float> buffer)
    {
        if (startSample < 0) startSample = 0;
        int n = Math.Min(buffer.Length, _samples.Length - startSample);
        if (n <= 0) return 0;
        _samples.AsSpan(startSample, n).CopyTo(buffer);
        return n;
    }

    private static (float[] samples, int sampleRate) Decode(byte[] b)
    {
        // Minimal RIFF/WAVE parse: find "fmt " and "data" chunks.
        if (b.Length < 12 || b[0] != 'R' || b[1] != 'I' || b[2] != 'F' || b[3] != 'F' ||
            b[8] != 'W' || b[9] != 'A' || b[10] != 'V' || b[11] != 'E')
            throw new InvalidDataException("Not a RIFF/WAVE file.");

        int pos = 12;
        int channels = 1, sampleRate = 48000, bitsPerSample = 16, audioFormat = 1;
        ReadOnlySpan<byte> data = default;

        while (pos + 8 <= b.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(b, pos, 4);
            int size = BitConverter.ToInt32(b, pos + 4);
            int body = pos + 8;
            if (size < 0 || body + size > b.Length) size = b.Length - body;

            if (id == "fmt ")
            {
                audioFormat = BitConverter.ToUInt16(b, body + 0);
                channels = BitConverter.ToUInt16(b, body + 2);
                sampleRate = BitConverter.ToInt32(b, body + 4);
                bitsPerSample = BitConverter.ToUInt16(b, body + 14);
            }
            else if (id == "data")
            {
                data = b.AsSpan(body, size);
            }

            pos = body + size + (size & 1); // chunks are word-aligned
        }

        if (data.IsEmpty) throw new InvalidDataException("WAV has no data chunk.");

        int bytesPerSample = bitsPerSample / 8;
        int frameCount = data.Length / (bytesPerSample * channels);
        var mono = new float[frameCount];

        for (int f = 0; f < frameCount; f++)
        {
            float sum = 0f;
            for (int c = 0; c < channels; c++)
            {
                int o = (f * channels + c) * bytesPerSample;
                sum += ReadSample(data, o, bitsPerSample, audioFormat);
            }
            mono[f] = sum / channels;
        }

        return (mono, sampleRate);
    }

    private static float ReadSample(ReadOnlySpan<byte> d, int o, int bits, int format)
    {
        // format 3 == IEEE float, 1 == PCM int
        if (format == 3 && bits == 32)
            return BitConverter.ToSingle(d.Slice(o, 4));

        return bits switch
        {
            8 => (d[o] - 128) / 128f,                                   // unsigned
            16 => BitConverter.ToInt16(d.Slice(o, 2)) / 32768f,
            24 => ((d[o] | (d[o + 1] << 8) | ((sbyte)d[o + 2] << 16))) / 8388608f,
            32 => BitConverter.ToInt32(d.Slice(o, 4)) / 2147483648f,
            _ => throw new NotSupportedException($"Unsupported WAV bit depth: {bits}"),
        };
    }
}
