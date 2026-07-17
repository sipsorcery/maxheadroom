using System;

namespace demo;

/// <summary>Reads PCM16 WAV input and converts it to the live microphone path's 8kHz mono shape.</summary>
public static class WavUtils
{
    public static short[] ReadMono8k(byte[] wav)
    {
        var (samples, sampleRate, channels) = ReadPcm16(wav);
        if (channels > 1)
        {
            var mono = new short[samples.Length / channels];
            for (var i = 0; i < mono.Length; i++)
            {
                var sum = 0;
                for (var channel = 0; channel < channels; channel++) sum += samples[i * channels + channel];
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

        var position = 12;
        var sampleRate = 0;
        var channels = 0;
        short[] samples = null;
        while (position + 8 <= wav.Length)
        {
            var chunkId = System.Text.Encoding.ASCII.GetString(wav, position, 4);
            var chunkSize = BitConverter.ToInt32(wav, position + 4);
            var body = position + 8;
            if (body > wav.Length || chunkSize < 0) throw new ArgumentException("invalid WAV chunk");

            if (chunkId == "fmt ")
            {
                if (body + 16 > wav.Length) throw new ArgumentException("invalid fmt chunk");
                var audioFormat = BitConverter.ToInt16(wav, body);
                channels = BitConverter.ToInt16(wav, body + 2);
                sampleRate = BitConverter.ToInt32(wav, body + 4);
                var bitsPerSample = BitConverter.ToInt16(wav, body + 14);
                if (audioFormat != 1 || bitsPerSample != 16)
                {
                    throw new ArgumentException($"only 16-bit PCM supported (format {audioFormat}, {bitsPerSample} bits)");
                }
            }
            else if (chunkId == "data")
            {
                var count = Math.Min(chunkSize, wav.Length - body) / 2;
                samples = new short[count];
                Buffer.BlockCopy(wav, body, samples, 0, count * 2);
            }

            position = body + chunkSize + (chunkSize & 1);
        }

        if (samples == null || sampleRate == 0 || channels <= 0)
        {
            throw new ArgumentException("missing fmt or data chunk");
        }
        return (samples, sampleRate, channels);
    }

    private static short[] Resample(short[] input, int fromRate, int toRate)
    {
        var outputLength = (long)input.Length * toRate / fromRate;
        var output = new short[outputLength];
        var step = (double)fromRate / toRate;
        for (long i = 0; i < outputLength; i++)
        {
            var sourcePosition = i * step;
            var lower = (int)sourcePosition;
            var fraction = sourcePosition - lower;
            var a = input[Math.Min(lower, input.Length - 1)];
            var b = input[Math.Min(lower + 1, input.Length - 1)];
            output[i] = (short)(a + fraction * (b - a));
        }
        return output;
    }
}
