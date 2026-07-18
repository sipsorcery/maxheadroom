//-----------------------------------------------------------------------------
// Filename: ISpeechRecognizer.cs
//
// Description: The listening surface the rest of the app drives. Decoded 8kHz 16-bit
// mono microphone PCM is pushed in via Write, and each final recognised utterance is
// raised on OnRecognized for the LLM->speak path. Implemented by the batch engines via
// the SpeechRecognizer base class (which segments locally), and by
// ElevenLabsStreamingSpeechRecognizer (which streams the audio to a server that does
// its own voice-activity detection).
//
// Author(s):
// Aaron Clauson (aaron@sipsorcery.com)
//
// License:
// BSD 3-Clause "New" or "Revised" License, see included LICENSE.md file.
//-----------------------------------------------------------------------------

using System;
using System.Threading.Tasks;

namespace demo;

public interface ISpeechRecognizer : IDisposable
{
    /// <summary>Raised with the final text of each recognised utterance (never empty/partial).</summary>
    event Action<string> OnRecognized;

    /// <summary>Initialises the engine and starts recognition; safe to call once.</summary>
    Task StartAsync();

    /// <summary>Pushes a block of decoded 8kHz 16-bit mono PCM into the recogniser.</summary>
    void Write(short[] pcm);

    /// <summary>Tells the recogniser whether the avatar is mid-reply. Only the streaming
    /// engine uses it: it gates its upstream while Max speaks (a concurrently-active scribe
    /// session throttles the ElevenLabs TTS stream on the same key, breaking lip-sync),
    /// re-opening on local voice activity so barge-in still works. Batch engines segment
    /// locally after the fact and ignore it.</summary>
    void SetAvatarSpeaking(bool speaking) { }
}
