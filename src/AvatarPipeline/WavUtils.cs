//-----------------------------------------------------------------------------
// Filename: WavUtils.cs
//
// Description: Minimal PCM WAV reader for the bench STT endpoint: accepts
// 16-bit PCM mono/stereo at common rates and produces the 8kHz mono stream
// ISpeechRecognizer implementations consume (the same shape the WebRTC audio
// path delivers after decode), so bench transcriptions exercise the live path.
//
// Author(s):
// sipsorcery-claude (aaron+claude@sipsorcery.com)
//
// License:
// BSD 3-Clause "New" or "Revised" License, see included LICENSE.md file.
//-----------------------------------------------------------------------------

using System;

namespace demo;

public static class WavUtils
{
    /// <summary>Parses a 16-bit PCM WAV and returns 8kHz mono samples.</summary>
    public static short[] ReadMono8k(byte[] wav)
    {
        var (samples, sampleRate, channels) = ReadPcm16(wav);

        if (channels > 1)
        {
            var mono = new short[samples.Length / channels];
            for (int i = 0; i < mono.Length; i++)
            {
                int sum = 0;
                for (int c = 0; c < channels; c++) { sum += samples[i * channels + c]; }
                mono[i] = (short)(sum / channels);
            }
            samples = mono;
        }

        return sampleRate == 8000 ? samples : Resample(samples, sampleRate, 8000);
    }

    private static (short[] samples, int sampleRate, int channels) ReadPcm16(byte[] wav)
    {
        if (wav.Length < 44 ||
            wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F' ||
            wav[8] != 'W' || wav[9] != 'A' || wav[10] != 'V' || wav[11] != 'E')
        {
            throw new ArgumentException("not a RIFF/WAVE file");
        }

        int pos = 12;
        int sampleRate = 0, channels = 0, bitsPerSample = 0;
        short[] samples = null;

        while (pos + 8 <= wav.Length)
        {
            var chunkId = System.Text.Encoding.ASCII.GetString(wav, pos, 4);
            int chunkSize = BitConverter.ToInt32(wav, pos + 4);
            int body = pos + 8;

            if (chunkId == "fmt ")
            {
                int audioFormat = BitConverter.ToInt16(wav, body);
                channels = BitConverter.ToInt16(wav, body + 2);
                sampleRate = BitConverter.ToInt32(wav, body + 4);
                bitsPerSample = BitConverter.ToInt16(wav, body + 14);
                if (audioFormat != 1 || bitsPerSample != 16)
                {
                    throw new ArgumentException($"only 16-bit PCM supported (format {audioFormat}, {bitsPerSample} bits)");
                }
            }
            else if (chunkId == "data")
            {
                int count = Math.Min(chunkSize, wav.Length - body) / 2;
                samples = new short[count];
                Buffer.BlockCopy(wav, body, samples, 0, count * 2);
            }

            pos = body + chunkSize + (chunkSize & 1);
        }

        if (samples == null || sampleRate == 0)
        {
            throw new ArgumentException("missing fmt or data chunk");
        }
        return (samples, sampleRate, channels);
    }

    /// <summary>Linear-interpolation resampler; adequate for speech-band bench audio.</summary>
    private static short[] Resample(short[] input, int fromRate, int toRate)
    {
        long outLen = (long)input.Length * toRate / fromRate;
        var output = new short[outLen];
        double step = (double)fromRate / toRate;
        for (long i = 0; i < outLen; i++)
        {
            double srcPos = i * step;
            int lo = (int)srcPos;
            double frac = srcPos - lo;
            short a = input[Math.Min(lo, input.Length - 1)];
            short b = input[Math.Min(lo + 1, input.Length - 1)];
            output[i] = (short)(a + frac * (b - a));
        }
        return output;
    }
}
