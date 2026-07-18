//-----------------------------------------------------------------------------
// Filename: OpenAiRealtimeSession.cs
//
// Description: A persistent server-to-server OpenAI Realtime WebSocket session.
// Browser microphone PCMU is forwarded directly to the Realtime API; returned PCMU
// is decoded once, then drives both WebRTC playback and the avatar mouth.
//-----------------------------------------------------------------------------

#nullable enable

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using demo.Performance;
using Microsoft.Extensions.Logging;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;

namespace demo;

public sealed class OpenAiRealtimeSession : IAsyncDisposable
{
    private static readonly ILogger logger = SIPSorcery.LogFactory.CreateLogger<OpenAiRealtimeSession>();
    private static readonly AudioFormat PcmuFormat = new(SDPWellKnownMediaFormatsEnum.PCMU);

    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _voice;
    private readonly string _transcriptionModel;
    private readonly int _silenceDurationMs;
    private readonly IAvatarMouth _renderer;
    private readonly AudioExtrasSource _audio;
    private readonly Func<BenchmarkTimeline?> _timelineProvider;
    private readonly ClientWebSocket _socket = new();
    private readonly AudioEncoder _decoder = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _askLock = new(1, 1);
    private readonly Channel<byte[]> _inputAudio = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(100)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
    private readonly Channel<OutputAudio> _outputAudio = Channel.CreateUnbounded<OutputAudio>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly ConcurrentDictionary<string, ResponseState> _responses = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Task? _senderTask;
    private Task? _receiverTask;
    private Task? _playerTask;
    private BenchmarkTimeline? _nextResponseTimeline;
    private TaskCompletionSource<string>? _pendingAsk;
    private string? _currentResponseId;
    private bool _disposed;

    public OpenAiRealtimeSession(
        string apiKey,
        string model,
        string voice,
        string transcriptionModel,
        int silenceDurationMs,
        IAvatarMouth renderer,
        AudioExtrasSource audio,
        Func<BenchmarkTimeline?> timelineProvider)
    {
        _apiKey = apiKey;
        _model = model;
        _voice = voice;
        _transcriptionModel = transcriptionModel;
        _silenceDurationMs = silenceDurationMs;
        _renderer = renderer;
        _audio = audio;
        _timelineProvider = timelineProvider;
    }

    public event Action<string>? InputTranscript;
    public event Action<string>? OutputTranscript;

    public bool IsConnected => _socket.State == WebSocketState.Open && _ready.Task.IsCompletedSuccessfully;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_socket.State != WebSocketState.None)
        {
            await _ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        _socket.Options.SetRequestHeader("Authorization", $"Bearer {_apiKey}");
        var uri = new Uri($"wss://api.openai.com/v1/realtime?model={Uri.EscapeDataString(_model)}");
        await _socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);

        _senderTask = Task.Run(() => SendInputAudioAsync(_stop.Token));
        _receiverTask = Task.Run(() => ReceiveAsync(_stop.Token));
        _playerTask = Task.Run(() => PlayOutputAudioAsync(_stop.Token));

        await SendAsync(new
        {
            type = "session.update",
            session = new
            {
                type = "realtime",
                instructions = LlmShared.SystemPrompt,
                output_modalities = new[] { "audio" },
                audio = new
                {
                    input = new
                    {
                        format = new { type = "audio/pcmu" },
                        transcription = new { model = _transcriptionModel, language = "en" },
                        turn_detection = new
                        {
                            type = "server_vad",
                            threshold = 0.5,
                            prefix_padding_ms = 300,
                            silence_duration_ms = _silenceDurationMs,
                            create_response = true,
                            interrupt_response = true,
                        },
                    },
                    output = new
                    {
                        format = new { type = "audio/pcmu" },
                        voice = _voice,
                    },
                },
            },
        }, cancellationToken).ConfigureAwait(false);

        await _ready.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        logger.LogInformation(
            "OpenAI Realtime session ready: model {Model}, voice {Voice}, input/output PCMU, VAD silence {SilenceMs}ms.",
            _model, _voice, _silenceDurationMs);
    }

    public void WritePcmu(ReadOnlySpan<byte> pcmu)
    {
        if (IsConnected && !pcmu.IsEmpty)
        {
            _inputAudio.Writer.TryWrite(pcmu.ToArray());
        }
    }

    public async Task<string> AskTextAsync(
        string prompt,
        BenchmarkTimeline? timeline = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return string.Empty;
        }

        await _askLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StartAsync(cancellationToken).ConfigureAwait(false);
            timeline?.RecordOnce(BenchmarkEventNames.PromptAccepted);

            var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingAsk = completion;
            _nextResponseTimeline = timeline;

            await SendAsync(new
            {
                type = "conversation.item.create",
                item = new
                {
                    type = "message",
                    role = "user",
                    content = new[] { new { type = "input_text", text = prompt } },
                },
            }, cancellationToken).ConfigureAwait(false);
            await SendAsync(new { type = "response.create" }, cancellationToken).ConfigureAwait(false);

            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pendingAsk = null;
            _nextResponseTimeline = null;
            _askLock.Release();
        }
    }

    private async Task SendInputAudioAsync(CancellationToken cancellationToken)
    {
        await foreach (var bytes in _inputAudio.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await SendAsync(new
            {
                type = "input_audio_buffer.append",
                audio = Convert.ToBase64String(bytes),
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ReceiveAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[32 * 1024];
        try
        {
            while (_socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var message = await ReceiveMessageAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (message == null)
                {
                    break;
                }
                HandleEvent(message);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception excp)
        {
            logger.LogError(excp, "OpenAI Realtime receive loop failed.");
            _ready.TrySetException(excp);
            _pendingAsk?.TrySetException(excp);
        }
    }

    private void HandleEvent(string message)
    {
        using var doc = JsonDocument.Parse(message);
        var root = doc.RootElement;
        var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;

        switch (type)
        {
            case "session.updated":
                _ready.TrySetResult();
                break;

            case "response.created":
            {
                var id = GetNestedString(root, "response", "id") ?? Guid.NewGuid().ToString("N");
                var timeline = Interlocked.Exchange(ref _nextResponseTimeline, null) ?? _timelineProvider();
                timeline?.RecordOnce(BenchmarkEventNames.LlmRequestStarted);
                timeline?.RecordOnce(BenchmarkEventNames.LlmResponseHeaders);
                _responses[id] = new ResponseState(timeline);
                _currentResponseId = id;
                break;
            }

            case "conversation.item.input_audio_transcription.completed":
            {
                var transcript = GetString(root, "transcript");
                if (!string.IsNullOrWhiteSpace(transcript))
                {
                    _timelineProvider()?.RecordOnce(BenchmarkEventNames.SttFinal);
                    InputTranscript?.Invoke(transcript.Trim());
                }
                break;
            }

            case "response.output_audio_transcript.delta":
            case "response.audio_transcript.delta":
            {
                var state = GetResponse(root);
                var delta = GetString(root, "delta");
                if (state != null && !string.IsNullOrEmpty(delta))
                {
                    state.Timeline?.RecordOnce(BenchmarkEventNames.LlmFirstToken);
                    state.Transcript.Append(delta);
                    if (!state.FirstSentence &&
                        delta.IndexOfAny(['.', '!', '?', '\n']) >= 0)
                    {
                        state.FirstSentence = true;
                        state.Timeline?.RecordOnce(BenchmarkEventNames.LlmFirstSentence);
                    }
                }
                break;
            }

            case "response.output_audio_transcript.done":
            case "response.audio_transcript.done":
            {
                var state = GetResponse(root);
                var transcript = GetString(root, "transcript");
                if (state != null && !string.IsNullOrWhiteSpace(transcript))
                {
                    state.Transcript.Clear();
                    state.Transcript.Append(transcript);
                }
                break;
            }

            case "response.output_audio.delta":
            case "response.audio.delta":
            {
                var id = GetResponseId(root);
                var state = GetResponse(id);
                var delta = GetString(root, "delta");
                if (state != null && !string.IsNullOrEmpty(delta))
                {
                    state.Timeline?.RecordOnce(BenchmarkEventNames.TtsAudioReady);
                    _outputAudio.Writer.TryWrite(new OutputAudio(id, Convert.FromBase64String(delta), false));
                }
                break;
            }

            case "response.output_audio.done":
            case "response.audio.done":
            {
                var id = GetResponseId(root);
                _outputAudio.Writer.TryWrite(new OutputAudio(id, Array.Empty<byte>(), true));
                break;
            }

            case "response.done":
            {
                var id = GetNestedString(root, "response", "id") ?? GetResponseId(root);
                var state = GetResponse(id);
                state?.Timeline?.RecordOnce(BenchmarkEventNames.LlmComplete);
                if (state != null)
                {
                    state.ResponseDone = true;
                    CompleteIfFinished(id, state);
                }
                break;
            }

            case "error":
                var error = GetNestedString(root, "error", "message") ?? message;
                logger.LogError("OpenAI Realtime API error: {Error}", error);
                _ready.TrySetException(new InvalidOperationException(error));
                _pendingAsk?.TrySetException(new InvalidOperationException(error));
                break;
        }
    }

    private async Task PlayOutputAudioAsync(CancellationToken cancellationToken)
    {
        string? speakingResponse = null;
        await foreach (var item in _outputAudio.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var state = GetResponse(item.ResponseId);
            if (state == null)
            {
                continue;
            }

            if (item.Done)
            {
                if (speakingResponse == item.ResponseId)
                {
                    _renderer.EndSpeech();
                    speakingResponse = null;
                }
                state.AudioDone = true;
                state.Timeline?.RecordOnce(BenchmarkEventNames.AudioComplete);
                CompleteIfFinished(item.ResponseId, state);
                continue;
            }

            if (speakingResponse != item.ResponseId)
            {
                if (speakingResponse != null)
                {
                    _renderer.EndSpeech();
                }
                _renderer.BeginSpeech();
                speakingResponse = item.ResponseId;
            }

            var pcm8k = _decoder.DecodeAudio(item.Pcmu, PcmuFormat);
            if (pcm8k.Length == 0)
            {
                continue;
            }

            // AudioExtrasSource accepts 16k PCM. Duplicate each 8k sample; this is deliberately
            // simple because the Realtime API remains PCMU end-to-end and Wav2Lip only needs the
            // same waveform timing for its mouth features.
            var pcm16k = new short[pcm8k.Length * 2];
            for (var i = 0; i < pcm8k.Length; i++)
            {
                pcm16k[i * 2] = pcm8k[i];
                pcm16k[i * 2 + 1] = pcm8k[i];
            }

            _renderer.PushAudio(pcm16k, 16000);
            state.Timeline?.RecordOnce(BenchmarkEventNames.AudioStarted);
            await _audio.SendAudioFromStream(ToStream(pcm16k), AudioSamplingRatesEnum.Rate16KHz)
                .ConfigureAwait(false);
        }
    }

    private void CompleteIfFinished(string responseId, ResponseState state)
    {
        if (!state.ResponseDone || !state.AudioDone)
        {
            return;
        }

        var transcript = state.Transcript.ToString().Trim();
        if (!string.IsNullOrWhiteSpace(transcript))
        {
            OutputTranscript?.Invoke(transcript);
        }
        _pendingAsk?.TrySetResult(transcript);
        _responses.TryRemove(responseId, out _);
    }

    private ResponseState? GetResponse(JsonElement root) => GetResponse(GetResponseId(root));

    private ResponseState? GetResponse(string id) =>
        !string.IsNullOrWhiteSpace(id) && _responses.TryGetValue(id, out var state) ? state : null;

    private string GetResponseId(JsonElement root) =>
        GetString(root, "response_id") ?? _currentResponseId ?? string.Empty;

    private async Task SendAsync(object payload, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task<string?> ReceiveMessageAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }
            stream.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? GetNestedString(JsonElement element, string parent, string name) =>
        element.TryGetProperty(parent, out var nested) ? GetString(nested, name) : null;

    private static MemoryStream ToStream(short[] pcm)
    {
        var bytes = new byte[pcm.Length * sizeof(short)];
        Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
        return new MemoryStream(bytes, writable: false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _stop.Cancel();
        _inputAudio.Writer.TryComplete();
        _outputAudio.Writer.TryComplete();

        if (_socket.State == WebSocketState.Open)
        {
            try
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "call ended", CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
            }
        }

        var tasks = new[] { _senderTask, _receiverTask, _playerTask }.Where(x => x != null).Cast<Task>();
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _socket.Dispose();
        _stop.Dispose();
        _sendLock.Dispose();
        _askLock.Dispose();
    }

    private sealed class ResponseState(BenchmarkTimeline? timeline)
    {
        public BenchmarkTimeline? Timeline { get; } = timeline;
        public StringBuilder Transcript { get; } = new();
        public bool FirstSentence { get; set; }
        public bool ResponseDone { get; set; }
        public bool AudioDone { get; set; }
    }

    private sealed record OutputAudio(string ResponseId, byte[] Pcmu, bool Done);
}
