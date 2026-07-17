//-----------------------------------------------------------------------------
// Filename: History.cs
//
// Description: Bench history generator. Reads every run JSON under
// <history-dir>/runs/, and regenerates <history-dir>/history.md (a most-recent-
// first table) plus charts/latency.svg and charts/wer.svg so trends are visible
// at a glance in the GitHub UI (which renders SVG in markdown natively).
// Invoked by the bench workflow after each run via `MaxBench history`.
//
// Author(s):
// sipsorcery-claude (aaron+claude@sipsorcery.com)
//
// License:
// BSD 3-Clause "New" or "Revised" License, see included LICENSE.md file.
//-----------------------------------------------------------------------------

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace demo.bench;

static class History
{
    private sealed record Run(
        DateTimeOffset Utc, string Label, string LlmModel,
        double? FirstAudioP50, double? LlmTtftP50, double? LlmFirstP50, double? LlmCompleteP50,
        double? TtsFirstChunkP50, double? TtsSynthP50, double? LipsyncFirstP50,
        double? RenderTickP95, double? RenderMouthP50, double? RenderComposeP50,
        double? Wav2LipP50, double? Wer);

    public static void Generate(string historyDir)
    {
        var runsDir = Path.Combine(historyDir, "runs");
        Directory.CreateDirectory(runsDir);
        Directory.CreateDirectory(Path.Combine(historyDir, "charts"));

        var runs = Directory.GetFiles(runsDir, "*.json")
            .Select(Parse)
            .Where(r => r != null)
            .OrderBy(r => r!.Utc)
            .Select(r => r!)
            .ToList();

        File.WriteAllText(Path.Combine(historyDir, "history.md"), BuildMarkdown(runs));
        File.WriteAllText(Path.Combine(historyDir, "charts", "latency.svg"), BuildChart(
            "Latency (ms, p50 unless noted) — lower is better", runs,
            [
                ("first audio (e2e)", "#d62728", r => r.FirstAudioP50),
                ("tts first chunk", "#1f77b4", r => r.TtsFirstChunkP50),
                ("llm ttft", "#ff7f0e", r => r.LlmTtftP50),
                ("llm first sentence", "#2ca02c", r => r.LlmFirstP50),
                ("lipsync first mouth", "#9467bd", r => r.LipsyncFirstP50),
                ("render tick p95", "#8c564b", r => r.RenderTickP95),
            ], "ms"));
        File.WriteAllText(Path.Combine(historyDir, "charts", "wer.svg"), BuildChart(
            "STT word error rate (%) — lower is better", runs,
            [("WER", "#d62728", r => r.Wer * 100 is double w ? w : null)], "%"));
        // Own chart/scale: render-loop stages are tens of ms, invisible on the latency
        // chart's multi-second axis. The 25fps budget line (40ms) is drawn for reference.
        File.WriteAllText(Path.Combine(historyDir, "charts", "render.svg"), BuildChart(
            "Render loop per-frame cost (ms, p50 unless noted) — 25fps budget is 40ms", runs,
            [
                ("render tick (p95)", "#8c564b", r => r.RenderTickP95),
                ("render mouth (wav2lip)", "#1f77b4", r => r.RenderMouthP50),
                ("render compose (SkiaSharp)", "#2ca02c", r => r.RenderComposeP50),
            ], "ms", budgetLine: 40));

        Console.WriteLine($"history: {runs.Count} runs -> {historyDir}/history.md + charts/");
    }

    private static Run Parse(string path)
    {
        try
        {
            var j = JsonNode.Parse(File.ReadAllText(path))!;
            var agg = j["ask"]?["server_aggregates"];
            var srv = j["server_metrics"];
            return new Run(
                DateTimeOffset.Parse(j["utc"]!.GetValue<string>(), CultureInfo.InvariantCulture),
                j["label"]?.GetValue<string>() ?? Path.GetFileNameWithoutExtension(path),
                j["llm_model"]?.GetValue<string>() ?? "—",
                P50FromList(j["ask"]?["first_audio_ms"]),
                Num(agg?["llm_ttft"]?["p50"]),
                Num(agg?["llm_first_sentence"]?["p50"]),
                Num(agg?["llm_stream_complete"]?["p50"]),
                Num(agg?["tts_first_chunk"]?["p50"] ?? srv?["tts_first_chunk"]?["p50"]),
                Num(agg?["tts_synth"]?["p50"]),
                Num(srv?["lipsync_first_mouth"]?["p50"]),
                Num(srv?["render_tick"]?["p95"]),
                Num(srv?["render_mouth"]?["p50"]),
                Num(srv?["render_compose"]?["p50"]),
                Num(srv?["wav2lip_infer"]?["p50"]),
                Num(j["stt"]?["overall_wer"]));
        }
        catch (Exception excp)
        {
            Console.Error.WriteLine($"history: skipping {Path.GetFileName(path)}: {excp.Message}");
            return null;
        }
    }

    private static double? Num(JsonNode n) => n == null ? null : n.GetValue<double>();

    private static double? P50FromList(JsonNode n)
    {
        if (n is not JsonArray arr || arr.Count == 0) { return null; }
        var sorted = arr.Select(v => v!.GetValue<double>()).OrderBy(v => v).ToArray();
        var rank = 0.5 * (sorted.Length - 1);
        int lo = (int)rank;
        return lo + 1 < sorted.Length ? sorted[lo] + (rank - lo) * (sorted[lo + 1] - sorted[lo]) : sorted[lo];
    }

    private static string BuildMarkdown(List<Run> runs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Max bench history");
        sb.AppendLine();
        sb.AppendLine("Regenerated by `.github/workflows/bench.yml` after every run. All latencies are p50 in ms.");
        sb.AppendLine();
        sb.AppendLine("![latency](charts/latency.svg)");
        sb.AppendLine();
        sb.AppendLine("![wer](charts/wer.svg)");
        sb.AppendLine();
        sb.AppendLine("![render](charts/render.svg)");
        sb.AppendLine();
        sb.AppendLine("| run (UTC) | label | LLM model | first audio (e2e) | llm ttft | llm first | tts first chunk | lipsync first | render tick p95 | render mouth | render compose | WER |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var r in Enumerable.Reverse(runs))
        {
            sb.AppendLine(
                $"| {r.Utc:yyyy-MM-dd HH:mm} | {r.Label} | {r.LlmModel} | {Fmt(r.FirstAudioP50)} | {Fmt(r.LlmTtftP50)} | " +
                $"{Fmt(r.LlmFirstP50)} | {Fmt(r.TtsFirstChunkP50)} | {Fmt(r.LipsyncFirstP50)} | {Fmt(r.RenderTickP95)} | " +
                $"{Fmt(r.RenderMouthP50)} | {Fmt(r.RenderComposeP50)} | " +
                $"{(r.Wer is double w ? w.ToString("P1", CultureInfo.InvariantCulture) : "—")} |");
        }
        sb.AppendLine();
        sb.AppendLine("## Metric definitions");
        sb.AppendLine();
        sb.AppendLine("All latency figures are the p50 (median) across the prompts/windows in one bench run, in milliseconds unless noted. See [bench/README.md](https://github.com/sipsorcery/maxheadroom/blob/master/bench/README.md) for how each is measured.");
        sb.AppendLine();
        sb.AppendLine("- **LLM model** — which model generated the replies for that run (`GET /version`'s `llmModel`: the in-process GGUF filename, or the configured endpoint model name). Model swaps are a config change, not a code change, so this is the only thing that tells two runs with the same commit label apart. Runs from before this field existed show `—`.");
        sb.AppendLine("- **first audio (e2e)** — *end-to-end LLM reply latency.* Wall-clock time from the bench posting a prompt to `/ask` until the first audible (non-silent) audio packet arrives over the WebRTC connection. The single number that best represents \"how long the viewer waits before Max starts talking.\"");
        sb.AppendLine("- **llm ttft** — time to the LLM's first token off the wire (`llm_ttft`). The purest measure of model/endpoint responsiveness; program target <400ms.");
        sb.AppendLine("- **llm first** — server-side time from prompt received to the first *sentence* of the LLM's reply becoming available for speech (`llm_first_sentence`). The gap above *llm ttft* is sentence-chunking cost - the wait for the whole first sentence to generate.");
        sb.AppendLine("- **tts first chunk** — time from an utterance starting until the first playable audio exists (`tts_first_chunk`). For blocking engines (sherpa) this equals whole-sentence synthesis; for streaming engines (ElevenLabs) it is the first websocket chunk. Program target <300ms. (Full synth cost remains in the run JSON as `tts_synth`.)");
        sb.AppendLine("- **lipsync first** — time from an utterance's audio being handed to the avatar renderer until the first lip-synced (Wav2Lip) mouth frame is ready (`lipsync_first_mouth`). Governs how quickly the avatar's mouth starts moving once it begins speaking.");
        sb.AppendLine("- **render tick p95** — 95th percentile of the whole per-frame render cost while speaking: mouth inference + frame compose + video encode (`render_tick`). The budget at 25fps is 40ms; a p95 above that means dropped frames and a laggy face.");
        sb.AppendLine("- **render mouth** — the render tick's mouth-inference stage (`render_mouth`): the Wav2Lip ONNX call (or reusing the last mouth when no new audio window is ready yet). Tracks `wav2lip_infer` closely; currently the single largest render-tick cost.");
        sb.AppendLine("- **render compose** — the render tick's SkiaSharp compositing stage (`render_compose`): matte blend, VHS grade, head-sway warp, blinks. All per-pixel managed code today - the second-largest render-tick cost and a live optimization target (see the [render chart](charts/render.svg) and the 40ms budget line).");
        sb.AppendLine("- **WER** — *word error rate* of speech-to-text. The bench sends a fixed reference audio clip (Harvard sentences, `bench/corpus.json`) through the same offline recogniser the live WebRTC audio path uses, then scores the transcript against the known-correct reference text. Lower is better; 0% is a perfect transcript.");
        return sb.ToString();

        static string Fmt(double? v) => v is double d ? d.ToString("F0", CultureInfo.InvariantCulture) : "—";
    }

    /// <summary>Minimal dependency-free SVG line chart: one polyline per series over run index.
    /// <paramref name="budgetLine"/> draws an optional dashed reference line (e.g. a frame
    /// budget) at that y-value.</summary>
    private static string BuildChart(string title, List<Run> runs,
        (string name, string color, Func<Run, double?> pick)[] series, string yUnit,
        double? budgetLine = null)
    {
        const int W = 900, H = 380, L = 70, R = 20, T = 44, B = 56;
        int plotW = W - L - R, plotH = H - T - B;

        var points = series
            .Select(s => (s.name, s.color, values: runs.Select(s.pick).ToArray()))
            .ToArray();
        double dataMax = points.SelectMany(p => p.values).Where(v => v != null).Select(v => v!.Value).DefaultIfEmpty(1).Max();
        double yMax = Math.Max(1, Math.Max(dataMax, budgetLine ?? 0)) * 1.08;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{W}\" height=\"{H}\" viewBox=\"0 0 {W} {H}\" font-family=\"sans-serif\">");
        sb.AppendLine($"<rect width=\"{W}\" height=\"{H}\" fill=\"white\"/>");
        sb.AppendLine($"<text x=\"{L}\" y=\"24\" font-size=\"16\" fill=\"#333\">{Esc(title)}</text>");

        // Y gridlines + labels.
        for (int i = 0; i <= 5; i++)
        {
            double val = yMax * i / 5;
            double y = T + plotH - plotH * i / 5.0;
            sb.AppendLine($"<line x1=\"{L}\" y1=\"{y:F1}\" x2=\"{W - R}\" y2=\"{y:F1}\" stroke=\"#e5e5e5\"/>");
            sb.AppendLine($"<text x=\"{L - 6}\" y=\"{y + 4:F1}\" font-size=\"11\" fill=\"#666\" text-anchor=\"end\">{val:F0}{Esc(yUnit)}</text>");
        }

        double X(int i) => runs.Count == 1 ? L + plotW / 2.0 : L + plotW * i / (double)(runs.Count - 1);
        double Y(double v) => T + plotH - plotH * v / yMax;

        if (budgetLine is double budget)
        {
            double by = Y(budget);
            sb.AppendLine($"<line x1=\"{L}\" y1=\"{by:F1}\" x2=\"{W - R}\" y2=\"{by:F1}\" stroke=\"#d62728\" stroke-width=\"1.5\" stroke-dasharray=\"6,4\"/>");
            sb.AppendLine($"<text x=\"{W - R - 4}\" y=\"{by - 5:F1}\" font-size=\"11\" fill=\"#d62728\" text-anchor=\"end\">budget {budget:F0}{Esc(yUnit)}</text>");
        }

        // X labels: first, middle and last run timestamps (edge labels anchored inward
        // so they don't clip outside the canvas).
        foreach (int i in new[] { 0, runs.Count / 2, runs.Count - 1 }.Distinct())
        {
            if (runs.Count == 0) { break; }
            string anchor = i == 0 ? "start" : i == runs.Count - 1 ? "end" : "middle";
            sb.AppendLine($"<text x=\"{X(i):F1}\" y=\"{H - B + 18}\" font-size=\"11\" fill=\"#666\" text-anchor=\"{anchor}\">{runs[i].Utc:MM-dd HH:mm}</text>");
        }

        // Series polylines, points and legend.
        for (int s = 0; s < points.Length; s++)
        {
            var (name, color, values) = points[s];
            var pts = string.Join(" ", values.Select((v, i) => v == null ? null : $"{X(i):F1},{Y(v.Value):F1}").Where(p => p != null));
            if (pts.Length > 0)
            {
                sb.AppendLine($"<polyline points=\"{pts}\" fill=\"none\" stroke=\"{color}\" stroke-width=\"2\"/>");
                for (int i = 0; i < values.Length; i++)
                {
                    if (values[i] != null)
                    {
                        sb.AppendLine($"<circle cx=\"{X(i):F1}\" cy=\"{Y(values[i]!.Value):F1}\" r=\"3\" fill=\"{color}\"/>");
                    }
                }
            }
            double lx = L + s * (plotW / (double)Math.Max(series.Length, 1));
            sb.AppendLine($"<rect x=\"{lx:F1}\" y=\"{H - 22}\" width=\"10\" height=\"10\" fill=\"{color}\"/>");
            sb.AppendLine($"<text x=\"{lx + 14:F1}\" y=\"{H - 13}\" font-size=\"11\" fill=\"#333\">{Esc(name)}</text>");
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    private static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
