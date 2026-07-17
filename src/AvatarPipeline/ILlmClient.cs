//-----------------------------------------------------------------------------
// Filename: ILlmClient.cs
//
// Description: The reply-generation contract the rest of the app drives. Two
// implementations: LocalLlmClient (HTTP to an OpenAI-compatible endpoint - Ollama,
// LM Studio, or a hosted gateway) and LlamaSharpLlmClient (llama.cpp IN-PROCESS via
// LLamaSharp - no external server to orchestrate). Both speak the same surface: a
// one-shot reply, and a sentence-by-sentence stream so the avatar can start talking
// before the full completion has been generated.
//
// Author(s):
// Aaron Clauson (aaron@sipsorcery.com)
//
// License:
// BSD 3-Clause "New" or "Revised" License, see included LICENSE.md file.
//-----------------------------------------------------------------------------

using System.Collections.Generic;
using System.Threading.Tasks;

namespace demo;

public interface ILlmClient
{
    /// <summary>False when no model/endpoint is configured - callers speak the prompt verbatim.</summary>
    bool IsConfigured { get; }

    /// <summary>Human-readable engine summary for startup logging.</summary>
    string Description { get; }

    /// <summary>Returns the whole in-character reply in one shot (falls back to the prompt on failure).</summary>
    Task<string> GenerateReplyAsync(string prompt);

    /// <summary>
    /// Pays any one-time first-inference costs up front (weights page-in, context allocation)
    /// so the first real reply isn't ~10s slower than the rest. No-op for HTTP clients - the
    /// remote server owns its own warm state.
    /// </summary>
    Task WarmUpAsync() => Task.CompletedTask;

    /// <summary>Streams the reply one sentence at a time as tokens arrive.</summary>
    IAsyncEnumerable<string> StreamReplyAsync(string prompt);
}

/// <summary>Bits shared by the LLM clients: the persona prompt and sentence chunking.</summary>
public static class LlmShared
{
    public const string SystemPrompt =
        "You are Max Headroom, the stuttering, wisecracking 1980s computer-generated TV host. " +
        "Reply in one or two short, punchy, slightly sarcastic sentences. Keep it light and witty. " +
        "Your reply is spoken aloud by a text-to-speech engine: plain spoken words only - never " +
        "use markdown, asterisks, underscores, emojis or stage directions.";

    /// <summary>
    /// Strips markdown the TTS engine would otherwise speak literally ("asterisk") from an
    /// LLM sentence. Defence in depth: the system prompt forbids markdown but smaller models
    /// routinely emit it anyway.
    /// </summary>
    public static string SanitizeForSpeech(string text)
    {
        if (string.IsNullOrEmpty(text)) { return text; }
        // Markdown links: keep the text, drop the URL.
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\[([^\]]*)\]\([^)]*\)", "$1");
        // Emphasis/code/heading markers. Asterisks in particular get spoken as "asterisk".
        text = text.Replace("*", string.Empty).Replace("`", string.Empty).Replace("#", string.Empty);
        // Leading/trailing underscore emphasis (leave intra-word underscores alone).
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(?<=^|\s)_+|_+(?=\s|$)", string.Empty);
        return text;
    }

    /// <summary>Below this many characters, a clause boundary is not taken as a split
    /// point - avoids handing the speaker fragments like "Well," on their own, which
    /// sound choppy and pay a full utterance's synthesis/renderer overhead for one word.</summary>
    private const int MinClauseChars = 20;

    /// <summary>
    /// Removes and returns the first complete chunk from <paramref name="buffer"/>: a full
    /// sentence (up to and including a '.', '!', '?' or newline, always split immediately),
    /// or - once at least <see cref="MinClauseChars"/> have accumulated - a clause (up to a
    /// ',', ';', ':' or em dash '—'). Splitting on clauses lets the avatar start speaking the
    /// first phrase of a reply instead of waiting for the whole first sentence to finish
    /// generating, which the bench measured as ~1.5s of the reply's time-to-first-audio.
    /// Plain hyphens are deliberately excluded: the persona's stutter ("s-s-suppose") uses
    /// them mid-word, not as a clause separator.
    /// Returns null when there is no split point yet, or an empty string when the removed
    /// span was blank.
    /// </summary>
    public static string TakeSentence(System.Text.StringBuilder buffer)
    {
        for (int i = 0; i < buffer.Length; i++)
        {
            char c = buffer[i];
            bool isSentenceEnd = c == '.' || c == '!' || c == '?' || c == '\n';
            bool isClauseEnd = c == ',' || c == ';' || c == ':' || c == '—' /* em dash */;

            if (isSentenceEnd || (isClauseEnd && i + 1 >= MinClauseChars))
            {
                int len = i + 1;
                var sentence = buffer.ToString(0, len).Trim();
                buffer.Remove(0, len);
                return sentence;
            }
        }

        return null;
    }
}
