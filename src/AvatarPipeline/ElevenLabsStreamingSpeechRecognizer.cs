//-----------------------------------------------------------------------------
// Filename: ElevenLabsStreamingSpeechRecognizer.cs
//
// Description: Low-latency cloud speech-to-text using the ElevenLabs realtime
// WebSocket API (/v1/speech-to-text/realtime, Scribe v2). Unlike the batch recognisers
// (SpeechRecognizer), this does NO local voice-activity detection: the decoded 8kHz mic
// PCM is streamed straight to the server, which runs its own VAD and returns committed
// (final) transcripts as the speaker finishes each utterance. That removes the
// buffer-until-silence delay and gives lower-latency listening.
//
// Flow:
//   * Write enqueues mic PCM blocks (non-blocking, called from the RTP receive path);
//   * a sender task coalesces ~100ms of audio and sends it as base64 "input_audio_chunk"
//     messages (audio_format=pcm_<sampleRate>, matching the mic decode rate - 16kHz on the
//     Opus track, so no resample is needed between the track and the socket);
//   * a receive loop raises OnRecognized on each "committed_transcript" (final) message;
//     partial transcripts are ignored.
//
// This is a prototype: the commit strategy / message field names are best verified against
// a live account. It is purely additive - the batch recognisers are untouched.
//
// Author(s):
// Aaron Clauson (aaron@sipsorcery.com)
//
// License:
// BSD 3-Clause "New" or "Revised" License, see included LICENSE.md file.
//-----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace demo;

public sealed class ElevenLabsStreamingSpeechRecognizer : ISpeechRecognizer
{
    private static readonly ILogger logger = SIPSorcery.LogFactory.CreateLogger<ElevenLabsStreamingSpeechRecognizer>();

    private readonly int _sampleRate;           // Rate of the PCM pushed into Write.
    private readonly int _sendBatchSamples;     // ~100ms of audio per WebSocket message.

    private readonly string _apiKey;
    private readonly string _modelId;
    private readonly string _commitStrategy;
    private readonly string _vadSilenceThresholdSecs;

    private readonly Channel<short[]> _audioQueue = Channel.CreateUnbounded<short[]>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();

    private ClientWebSocket _ws;
    private Task _sender;
    private Task _receiver;
    private bool _started;
    private bool _disposed;
    private int _otherMessages;

    // Reply gate: while the avatar speaks we stop streaming to scribe (a concurrently-active
    // scribe session throttles the ElevenLabs TTS stream on the same API key, which starves
    // the lip-sync mel pipeline). A local energy VAD keeps watching the mic; real speech over
    // Max re-opens the gate (barge-in), flushing a short ring buffer so the utterance onset
    // isn't clipped. Gated-off audio is never sent, so scribe sees a quiet session.
    private const double GateSpeechRmsThreshold = 350.0;      // Matches the batch VAD's.
    private static readonly TimeSpan GateSpeechHold = TimeSpan.FromMilliseconds(800);
    private readonly int _gateRingCapacity;                   // ~500ms of PCM in samples.
    private readonly Queue<short[]> _gateRing = new();
    private int _gateRingSamples;
    private volatile bool _avatarSpeaking;
    private long _lastLocalSpeechTicks;

    public event Action<string> OnRecognized;

    /// <param name="apiKey">ElevenLabs API key (sent as the xi-api-key header).</param>
    /// <param name="modelId">Realtime STT model id (default "scribe_v2_realtime").</param>
    /// <param name="commitStrategy">Server commit strategy; "vad" lets the server segment utterances.</param>
    /// <param name="sampleRate">Rate of the PCM pushed into Write; must match an audio_format
    /// the realtime API accepts (pcm_8000 / pcm_16000).</param>
    public ElevenLabsStreamingSpeechRecognizer(string apiKey, string modelId = "scribe_v2_realtime", string commitStrategy = "vad", int sampleRate = 8000)
    {
        _apiKey = apiKey;
        _modelId = string.IsNullOrWhiteSpace(modelId) ? "scribe_v2_realtime" : modelId;
        _commitStrategy = string.IsNullOrWhiteSpace(commitStrategy) ? "vad" : commitStrategy;
        _sampleRate = sampleRate;
        _sendBatchSamples = sampleRate / 10;
        _gateRingCapacity = sampleRate / 2;

        // Server-side VAD silence window before it commits an utterance. The API default
        // (1.5s) is far longer than the trailing silence a WebRTC mic actually streams
        // after speech (Chrome stops/cuts RTP on the silent tail), so without this the
        // commit never arrives. ELEVENLABS_STT_VAD_SILENCE_SECS overrides; keep it below
        // the ~600ms of trailing silence the bench streams.
        _vadSilenceThresholdSecs =
            double.TryParse(Environment.GetEnvironmentVariable("ELEVENLABS_STT_VAD_SILENCE_SECS"),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var secs) && secs >= 0.1 && secs <= 2.0
                ? secs.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "0.5";
    }

    public async Task StartAsync()
    {
        if (_started)
        {
            return;
        }
        _started = true;

        _ws = new ClientWebSocket();
        _ws.Options.SetRequestHeader("xi-api-key", _apiKey);

        var uri = new Uri($"wss://api.elevenlabs.io/v1/speech-to-text/realtime" +
                          $"?model_id={_modelId}&audio_format=pcm_{_sampleRate}&commit_strategy={_commitStrategy}" +
                          $"&vad_silence_threshold_secs={_vadSilenceThresholdSecs}");
        await _ws.ConnectAsync(uri, _cts.Token).ConfigureAwait(false);

        _sender = Task.Run(SendLoopAsync);
        _receiver = Task.Run(ReceiveLoopAsync);
        logger.LogInformation("ElevenLabs realtime speech recognition started (model {Model}). Speak to the avatar.", _modelId);
    }

    public void SetAvatarSpeaking(bool speaking)
    {
        _avatarSpeaking = speaking;
        if (!speaking)
        {
            // Reply over: flush any ring-buffered onset audio and listen normally again.
            FlushGateRing();
        }
    }

    public void Write(short[] pcm)
    {
        if (_disposed || !_started || pcm == null || pcm.Length == 0)
        {
            return;
        }

        if (_avatarSpeaking)
        {
            if (Rms(pcm) > GateSpeechRmsThreshold)
            {
                // Someone is talking over Max - barge-in. Re-open the upstream, leading
                // with the buffered onset so scribe hears the start of the utterance.
                Interlocked.Exchange(ref _lastLocalSpeechTicks, DateTime.UtcNow.Ticks);
                FlushGateRing();
            }
            else if (DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastLocalSpeechTicks) > GateSpeechHold.Ticks)
            {
                // Max speaking, user quiet: hold the gate closed, keeping a short ring of
                // recent mic audio so a barge-in onset survives the VAD reaction time.
                lock (_gateRing)
                {
                    _gateRing.Enqueue(pcm);
                    _gateRingSamples += pcm.Length;
                    while (_gateRingSamples > _gateRingCapacity && _gateRing.Count > 0)
                    {
                        _gateRingSamples -= _gateRing.Dequeue().Length;
                    }
                }
                return;
            }
        }

        _audioQueue.Writer.TryWrite(pcm);
    }

    private void FlushGateRing()
    {
        lock (_gateRing)
        {
            while (_gateRing.Count > 0)
            {
                _audioQueue.Writer.TryWrite(_gateRing.Dequeue());
            }
            _gateRingSamples = 0;
        }
    }

    /// <summary>Root-mean-square amplitude of a 16-bit PCM block; cheap speech/silence test.</summary>
    private static double Rms(short[] pcm)
    {
        double sumSq = 0;
        for (int i = 0; i < pcm.Length; i++)
        {
            double s = pcm[i];
            sumSq += s * s;
        }
        return Math.Sqrt(sumSq / pcm.Length);
    }

    /// <summary>Coalesces queued PCM into ~100ms batches and sends them as base64 audio-chunk messages.</summary>
    private async Task SendLoopAsync()
    {
        var batch = new List<short>(_sendBatchSamples);
        try
        {
            await foreach (var pcm in _audioQueue.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                batch.AddRange(pcm);
                if (batch.Count >= _sendBatchSamples)
                {
                    await SendAudioAsync(batch.ToArray()).ConfigureAwait(false);
                    batch.Clear();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception excp)
        {
            logger.LogError(excp, "ElevenLabs STT sender failed.");
        }
    }

    private async Task SendAudioAsync(short[] pcm)
    {
        var bytes = new byte[pcm.Length * sizeof(short)];
        Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);

        var payload = JsonSerializer.Serialize(new
        {
            message_type = "input_audio_chunk",
            audio_base_64 = Convert.ToBase64String(bytes),
            sample_rate = _sampleRate,
        });

        var frame = Encoding.UTF8.GetBytes(payload);
        await _ws.SendAsync(new ArraySegment<byte>(frame), WebSocketMessageType.Text, true, _cts.Token).ConfigureAwait(false);
    }

    /// <summary>Reads transcript messages; raises OnRecognized on each committed (final) transcript.</summary>
    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (_ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                var message = await ReceiveMessageAsync(buffer).ConfigureAwait(false);
                if (message == null)
                {
                    break; // socket closed.
                }

                using var doc = JsonDocument.Parse(message);
                var root = doc.RootElement;
                if (!root.TryGetProperty("message_type", out var typeEl))
                {
                    continue;
                }

                var type = typeEl.GetString();
                if (type == "committed_transcript" || type == "committed_transcript_with_timestamps")
                {
                    var text = root.TryGetProperty("text", out var t) ? t.GetString()?.Trim() : null;
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        logger.LogInformation("Recognised: \"{Text}\"", text);
                        OnRecognized?.Invoke(text);
                    }
                }
                else if (type is "error" or "auth_error" or "quota_exceeded")
                {
                    logger.LogError("ElevenLabs STT error: {Message}", message);
                }
                else if (type == "session_started")
                {
                    logger.LogInformation("ElevenLabs STT session: {Message}", message);
                }
                else
                {
                    // partial_transcript and anything unexpected: visibility for diagnosing
                    // "connected but silent" failures without flooding the log per chunk.
                    if (++_otherMessages <= 10 || _otherMessages % 50 == 0)
                    {
                        logger.LogDebug("ElevenLabs STT message {Type}: {Message}", type, message);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception excp)
        {
            logger.LogError(excp, "ElevenLabs STT receiver failed.");
        }
    }

    /// <summary>Reads one (possibly multi-frame) text message off the socket; null if it closed.</summary>
    private async Task<string> ReceiveMessageAsync(byte[] buffer)
    {
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }
            ms.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        try { _cts.Cancel(); } catch { /* best effort */ }
        _audioQueue.Writer.TryComplete();

        try { Task.WhenAll(_sender ?? Task.CompletedTask, _receiver ?? Task.CompletedTask).Wait(TimeSpan.FromSeconds(2)); }
        catch { /* best effort */ }

        try
        {
            if (_ws is { State: WebSocketState.Open })
            {
                _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None).Wait(TimeSpan.FromSeconds(2));
            }
        }
        catch { /* best effort */ }

        _ws?.Dispose();
        _cts.Dispose();
    }
}
