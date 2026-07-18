//-----------------------------------------------------------------------------
// Filename: LipSyncTtsSpeaker.cs
//
// Description: Base class for the avatar's text-to-speech "speakers". It owns the
// shared pipeline - serialise utterances, resample to 16kHz, stream the PCM to the
// WebRTC audio track, and hand the audio to the IAvatarRenderer that animates the
// face - so each concrete engine only has to implement SynthesiseAsync (text in,
// 16-bit mono PCM + sample rate out). Implementations: SherpaTtsSpeaker (local,
// in-process) and ElevenLabsTtsSpeaker (cloud).
//
// SpeakQueueAsync pipelines a multi-clause reply: it prefetches clause N+1's
// synthesis while clause N is still playing, instead of the strictly serial
// synthesise-then-play-then-synthesise-next-clause pattern SpeakAsync uses alone.
// This was evaluated as sherpa-onnx's GenerateWithCallback (issue #13) first: that
// API only invokes its callback once per *sentence* the model's own text splitter
// produces (measured empirically - a single already-short clause, which is what
// AskAsync's clause chunking already hands each SpeakAsync call, yields exactly one
// callback at the very end, identical to the blocking Generate() call). VITS/Piper
// is a non-autoregressive model, so there is no partial audio to stream *within* one
// clause. The real remaining latency is *between* clauses in a multi-clause reply,
// which prefetching removes.
//
// Author(s):
// Aaron Clauson (aaron@sipsorcery.com)
//
// License:
// BSD 3-Clause "New" or "Revised" License, see included LICENSE.md file.
//-----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;

namespace demo;

public abstract class LipSyncTtsSpeaker : IAvatarSpeaker
{
    private static readonly ILogger logger = SIPSorcery.LogFactory.CreateLogger<LipSyncTtsSpeaker>();

    protected const int TargetRate = 16000;      // SendAudioFromStream consumes 16kHz mono PCM.
    private const int EnvelopeFrameMs = 30;      // Audio window granularity handed to the renderer.

    /// <summary>Shared HTTP client for the HTTP-based engines (ElevenLabs).</summary>
    protected static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly IAvatarMouth _renderer;
    private readonly AudioExtrasSource _audio;
    private readonly int _visemeLeadMs;
    private readonly SemaphoreSlim _speakLock = new(1, 1);

    protected LipSyncTtsSpeaker(IAvatarMouth renderer, AudioExtrasSource audio, int visemeLeadMs)
    {
        _renderer = renderer;
        _audio = audio;
        _visemeLeadMs = visemeLeadMs;
    }

    /// <summary>Short name of the concrete engine, used in log messages.</summary>
    protected abstract string EngineName { get; }

    /// <summary>Synthesises <paramref name="text"/>, returning 16-bit mono PCM and its sample rate.</summary>
    protected abstract Task<(short[] samples, int sampleRate)> SynthesiseAsync(string text);

    /// <summary>
    /// Synthesises <paramref name="text"/> and plays it through the avatar with amplitude-driven
    /// lip-sync. Only one utterance is spoken at a time.
    /// </summary>
    public async Task SpeakAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        await _speakLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var (pcm, sampleRate) = await SynthesiseTimedAsync(text).ConfigureAwait(false);
            await PlaySamplesAsync(pcm, sampleRate, text).ConfigureAwait(false);
        }
        finally
        {
            _speakLock.Release();
        }
    }

    /// <summary>
    /// Speaks a multi-clause reply, prefetching each clause's synthesis while the previous
    /// clause is still playing (see the file header) - the LLM-side producer (<paramref
    /// name="texts"/>, typically AskAsync's clause-chunked stream) can keep yielding while a
    /// clause plays; this pulls at most one clause ahead. Only one utterance/queue is spoken
    /// at a time (shares <see cref="SpeakAsync"/>'s lock).
    /// </summary>
    public async Task SpeakQueueAsync(IAsyncEnumerable<string> texts, Func<bool> shouldContinue = null)
    {
        if (texts == null)
        {
            return;
        }

        await _speakLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var enumerator = texts.GetAsyncEnumerator();
            if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
            {
                return;
            }

            string currentText = enumerator.Current;
            (short[] samples, int sampleRate) current = await TrySynthesiseAsync(currentText).ConfigureAwait(false);

            while (true)
            {
                if (shouldContinue != null && !shouldContinue())
                {
                    return;
                }

                // Begin waiting for and synthesising the next clause before playing this one.
                // Crucially, do not await it yet: first-clause playback must not be delayed while
                // the LLM is still producing clause two.
                async Task<(bool hasNext, string text, short[] samples, int sampleRate)> PrepareNextAsync()
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        return (false, null, null, 0);
                    }

                    string text = enumerator.Current;
                    var synthesised = await TrySynthesiseAsync(text).ConfigureAwait(false);
                    return (true, text, synthesised.samples, synthesised.sampleRate);
                }
                var nextTask = PrepareNextAsync();

                try
                {
                    await PlaySamplesAsync(current.samples, current.sampleRate, currentText).ConfigureAwait(false);
                }
                catch (Exception excp)
                {
                    logger.LogError(excp, "Exception {Engine}.SpeakQueueAsync playing \"{Text}\".", EngineName, currentText);
                }

                // Any synthesis time not hidden behind playback is the audible inter-clause gap.
                var clauseClock = Stopwatch.StartNew();
                var next = await nextTask.ConfigureAwait(false);
                if (!next.hasNext)
                {
                    break;
                }
                BenchMetrics.Record("tts_interclause_gap", clauseClock.Elapsed.TotalMilliseconds);

                // Barge-in may have happened while the prefetched clause was synthesising.
                // Never play that stale clause after the new turn has cancelled current audio.
                if (shouldContinue != null && !shouldContinue())
                {
                    return;
                }

                currentText = next.text;
                current = (next.samples, next.sampleRate);
            }
        }
        finally
        {
            _speakLock.Release();
        }
    }

    private async Task<(short[] samples, int sampleRate)> TrySynthesiseAsync(string text)
    {
        try
        {
            return await SynthesiseTimedAsync(text).ConfigureAwait(false);
        }
        catch (Exception excp)
        {
            logger.LogError(excp, "Exception {Engine}.SpeakQueueAsync synthesising \"{Text}\".", EngineName, text);
            return (null, 0);
        }
    }

    /// <summary>Synthesises one clause, recording the same tts_synth/tts_first_chunk bench
    /// stages <see cref="SpeakAsync"/> always has, whether called from there or from the
    /// prefetch loop in <see cref="SpeakQueueAsync"/>.</summary>
    private async Task<(short[] samples, int sampleRate)> SynthesiseTimedAsync(string text)
    {
        logger.LogInformation("[{Engine}] Synthesising: \"{Text}\"", EngineName, text);
        var synthClock = Stopwatch.StartNew();
        var result = await SynthesiseAsync(text).ConfigureAwait(false);
        BenchMetrics.Record("tts_synth", synthClock.Elapsed.TotalMilliseconds, $"chars={text.Length}");
        // For blocking engines the first playable audio IS the completed synthesis;
        // recorded under the same name the streaming engines use so the history
        // table compares time-to-first-audio across engines directly.
        BenchMetrics.Record("tts_first_chunk", synthClock.Elapsed.TotalMilliseconds);
        return result;
    }

    /// <summary>Plays already-synthesised PCM through the avatar with amplitude-driven lip-sync.
    /// Caller holds <see cref="_speakLock"/> for the duration.</summary>
    private async Task PlaySamplesAsync(short[] pcm, int sampleRate, string textForLogging)
    {
        if (pcm == null || pcm.Length == 0)
        {
            logger.LogError("[{Engine}] produced no audio for \"{Text}\".", EngineName, textForLogging);
            return;
        }

        var samples = sampleRate == TargetRate ? pcm : Resample(pcm, sampleRate, TargetRate);

        logger.LogInformation("[{Engine}] Synthesised {Samples} samples ({Ms} ms).",
            EngineName, samples.Length, samples.Length * 1000 / TargetRate);

        try
        {
            _renderer.BeginSpeech();

            int frameSamples = TargetRate * EnvelopeFrameMs / 1000;
            Task mouthTask = Task.CompletedTask;

            if (_renderer.PacesAudioInternally)
            {
                // The renderer paces itself (the Wav2Lip head): hand it the WHOLE utterance up
                // front so its model's look-ahead never waits on real-time delivery, then give
                // the slower video path a head start before the audio track plays.
                for (int start = 0; start < samples.Length; start += frameSamples)
                {
                    int count = Math.Min(frameSamples, samples.Length - start);
                    _renderer.PushAudio(new ReadOnlySpan<short>(samples, start, count), TargetRate);
                }
                if (_visemeLeadMs > 0)
                {
                    await Task.Delay(_visemeLeadMs).ConfigureAwait(false);
                }
            }
            else
            {
                // The renderer reacts instantly (cartoon): walk the audio in real time alongside
                // playback, leading by _visemeLeadMs so the face lands in sync once the slower
                // video path reaches the viewer.
                var stopwatch = Stopwatch.StartNew();
                mouthTask = Task.Run(async () =>
                {
                    for (int start = 0, i = 0; start < samples.Length; start += frameSamples, i++)
                    {
                        var delay = (long)i * EnvelopeFrameMs - _visemeLeadMs - stopwatch.ElapsedMilliseconds;
                        if (delay > 0)
                        {
                            await Task.Delay((int)delay).ConfigureAwait(false);
                        }
                        int count = Math.Min(frameSamples, samples.Length - start);
                        _renderer.PushAudio(new ReadOnlySpan<short>(samples, start, count), TargetRate);
                    }
                });
            }

            await _audio.SendAudioFromStream(ToStream(samples), AudioSamplingRatesEnum.Rate16KHz)
                .ConfigureAwait(false);

            await mouthTask.ConfigureAwait(false);
        }
        finally
        {
            _renderer.EndSpeech();
        }
    }

    /// <summary>Linear resample of 16-bit mono PCM from <paramref name="srcRate"/> to <paramref name="dstRate"/>.</summary>
    protected static short[] Resample(short[] src, int srcRate, int dstRate)
    {
        if (src.Length == 0 || srcRate == dstRate)
        {
            return src;
        }

        long outLen = (long)src.Length * dstRate / srcRate;
        var dst = new short[outLen];
        double step = (double)srcRate / dstRate;

        for (long i = 0; i < outLen; i++)
        {
            double srcPos = i * step;
            int i0 = (int)srcPos;
            int i1 = Math.Min(i0 + 1, src.Length - 1);
            double frac = srcPos - i0;
            dst[i] = (short)(src[i0] * (1 - frac) + src[i1] * frac);
        }
        return dst;
    }

    private static MemoryStream ToStream(short[] samples)
    {
        var bytes = new byte[samples.Length * sizeof(short)];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return new MemoryStream(bytes);
    }
}
