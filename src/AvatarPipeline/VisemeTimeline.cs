using System;
using System.Collections.Generic;

namespace demo;

public readonly record struct VisemeCue(double StartSeconds, double EndSeconds, string Name, float Weight = 0.8f);

public interface IVisemeTimelineReceiver
{
    void SetVisemeTimeline(IReadOnlyList<VisemeCue> cues);
}

public interface IVisemeTimelineProvider
{
    IReadOnlyList<VisemeCue> Build(string text, double durationSeconds);
}

/// <summary>Small text/duration fallback used when a TTS engine does not expose phoneme timings.</summary>
public sealed class TextVisemeTimelineProvider : IVisemeTimelineProvider
{
    public static readonly TextVisemeTimelineProvider Instance = new();

    public IReadOnlyList<VisemeCue> Build(string text, double durationSeconds)
    {
        var names = new List<string>();
        string normalized = (text ?? string.Empty).ToLowerInvariant();
        for (int i = 0; i < normalized.Length; i++)
        {
            char c = normalized[i];
            if (char.IsWhiteSpace(c) || char.IsPunctuation(c)) { continue; }
            if (i + 1 < normalized.Length && normalized.AsSpan(i, 2).Equals("th", StringComparison.Ordinal))
            {
                names.Add("TH"); i++; continue;
            }
            if (i + 1 < normalized.Length && normalized.AsSpan(i, 2).Equals("ch", StringComparison.Ordinal))
            {
                names.Add("CH"); i++; continue;
            }
            names.Add(MapCharacter(c));
        }
        if (names.Count == 0 || durationSeconds <= 0) { return Array.Empty<VisemeCue>(); }
        double slot = durationSeconds / names.Count;
        double overlap = Math.Min(slot * 0.18, 0.035);
        var cues = new List<VisemeCue>(names.Count);
        for (int i = 0; i < names.Count; i++)
        {
            double start = Math.Max(0, i * slot - overlap);
            double end = Math.Min(durationSeconds, (i + 1) * slot + overlap);
            cues.Add(new VisemeCue(start, end, names[i]));
        }
        return cues;
    }

    public IReadOnlyList<VisemeCue> BuildAligned(IReadOnlyList<string> characters,
        IReadOnlyList<double> starts, IReadOnlyList<double> ends)
    {
        int count = Math.Min(characters?.Count ?? 0, Math.Min(starts?.Count ?? 0, ends?.Count ?? 0));
        var cues = new List<VisemeCue>(count);
        for (int i = 0; i < count; i++)
        {
            if (string.IsNullOrWhiteSpace(characters[i])) { continue; }
            string name = MapCharacter(char.ToLowerInvariant(characters[i][0]));
            if (ends[i] <= starts[i]) { continue; }
            cues.Add(new VisemeCue(Math.Max(0, starts[i]), ends[i], name));
        }
        return cues;
    }

    private static string MapCharacter(char c) => c switch
    {
        'a' => "AA", 'e' or 'i' or 'y' => "IH", 'o' => "OH", 'u' or 'w' => "OU",
        'f' or 'v' => "ff", 's' or 'z' or 'x' => "ss", 't' => "TH", 'c' or 'j' => "CH",
        'b' or 'm' or 'p' => "pp", 'd' => "dd", 'k' or 'q' or 'g' => "kk", 'n' => "nn",
        'r' => "rr", _ => "AA"
    };
}

public static class VisemeTimelineBuilder
{
    public static IReadOnlyList<VisemeCue> Build(string text, double durationSeconds) =>
        TextVisemeTimelineProvider.Instance.Build(text, durationSeconds);
}
