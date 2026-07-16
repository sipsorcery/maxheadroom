#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace demo.Performance;

public static class BenchmarkTextMetrics
{
    public static double WordErrorRate(string reference, string hypothesis) =>
        ErrorRate(TokenizeWords(reference), TokenizeWords(hypothesis));

    public static double CharacterErrorRate(string reference, string hypothesis) =>
        ErrorRate(Normalize(reference).Replace(" ", string.Empty, StringComparison.Ordinal).ToCharArray(),
            Normalize(hypothesis).Replace(" ", string.Empty, StringComparison.Ordinal).ToCharArray());

    public static string Normalize(string text)
    {
        if (text == null) throw new ArgumentNullException(nameof(text));

        var normalized = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var c in text.ToLower(CultureInfo.InvariantCulture))
        {
            if (char.IsLetterOrDigit(c) || c == '\'')
            {
                if (pendingSpace && normalized.Length > 0)
                {
                    normalized.Append(' ');
                }
                normalized.Append(c);
                pendingSpace = false;
            }
            else
            {
                pendingSpace = true;
            }
        }
        return normalized.ToString();
    }

    private static string[] TokenizeWords(string text) =>
        Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static double ErrorRate<T>(IReadOnlyList<T> reference, IReadOnlyList<T> hypothesis)
    {
        if (reference.Count == 0)
        {
            return hypothesis.Count == 0 ? 0d : 1d;
        }

        var previous = new int[hypothesis.Count + 1];
        var current = new int[hypothesis.Count + 1];
        for (var j = 0; j <= hypothesis.Count; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= reference.Count; i++)
        {
            current[0] = i;
            for (var j = 1; j <= hypothesis.Count; j++)
            {
                var substitution = previous[j - 1] + (EqualityComparer<T>.Default.Equals(reference[i - 1], hypothesis[j - 1]) ? 0 : 1);
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), substitution);
            }
            (previous, current) = (current, previous);
        }

        return (double)previous[hypothesis.Count] / reference.Count;
    }
}
