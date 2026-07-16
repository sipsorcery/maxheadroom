#nullable enable

using System;
using System.Collections.Generic;

namespace demo.Performance;

/// <summary>
/// Stable event names shared by server instrumentation and benchmark clients.
/// Names are deliberately transport-neutral and use snake case so JSON results
/// remain compatible as individual benchmark implementations evolve.
/// </summary>
public static class BenchmarkEventNames
{
    public const string PromptAccepted = "prompt_accepted";
    public const string SpeechEnd = "speech_end";
    public const string SttFinal = "stt_final";
    public const string LlmRequestStarted = "llm_request_started";
    public const string LlmResponseHeaders = "llm_response_headers";
    public const string LlmFirstToken = "llm_first_token";
    public const string LlmFirstSentence = "llm_first_sentence";
    public const string LlmComplete = "llm_complete";
    public const string TtsAudioReady = "tts_audio_ready";
    public const string AudioStarted = "audio_started";
    public const string FirstMouthFrame = "first_mouth_frame";

    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(new[]
    {
        SpeechEnd,
        SttFinal,
        LlmRequestStarted,
        LlmResponseHeaders,
        LlmFirstToken,
        LlmFirstSentence,
        LlmComplete,
        TtsAudioReady,
        AudioStarted,
        FirstMouthFrame,
    });
}
