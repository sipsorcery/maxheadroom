//-----------------------------------------------------------------------------
// Filename: Program.cs
//
// Description: Bench client for the Max avatar. Three measurements against a
// running instance (requires BENCH_ENDPOINTS=true on the server):
//
//   ask    - POST /ask with a fixed prompt set; measures client-observed time
//            to the LLM reply plus server-side llm_first_sentence /
//            llm_stream_complete / tts_synth stage timings.
//   stt    - streams corpus WAVs (bench/corpus.json) to /bench/stt and scores
//            word error rate against their reference transcripts.
//   report - fetches /bench/metrics and prints per-stage aggregates (includes
//            lipsync_first_mouth and wav2lip_infer gathered during ask runs
//            with a connected WebRTC viewer, or organically from real use).
//   all    - ask + stt + report.
//
// Output: markdown summary on stdout, machine-readable JSON via --json <path>.
//
// Usage: dotnet run --project src/MaxBench -- <mode> --target https://max-claude.sipsorcery.com [--json out.json]
//
// Author(s):
// sipsorcery-claude (aaron+claude@sipsorcery.com)
//
// License:
// BSD 3-Clause "New" or "Revised" License, see included LICENSE.md file.
//-----------------------------------------------------------------------------

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

var mode = args.Length > 0 ? args[0] : "all";
string target = GetOpt("--target") ?? Environment.GetEnvironmentVariable("BENCH_TARGET") ?? "http://localhost:8080";
string jsonOut = GetOpt("--json");
int askIterations = int.TryParse(GetOpt("--iterations"), out var it) ? it : 5;
target = target.TrimEnd('/');

var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
var results = new JsonObject { ["target"] = target, ["utc"] = DateTime.UtcNow.ToString("o"), ["mode"] = mode };
var summary = new StringBuilder();
summary.AppendLine($"# Max bench — {target}");
summary.AppendLine();

// Fixed prompt set: stable across runs so latency numbers are comparable.
string[] prompts =
[
    "In one short sentence, who are you?",
    "Say hello to the viewers in ten words or less.",
    "What year is it? Answer briefly.",
    "Give me a one-line fact about television.",
    "In one sentence, what do you think about computers?",
];

int exitCode = 0;
try
{
    if (mode is "ask" or "all") { await RunAsk(); }
    if (mode is "stt" or "all") { await RunStt(); }
    if (mode is "report" or "all") { await RunReport(); }
}
catch (Exception excp)
{
    summary.AppendLine($"**BENCH FAILED**: {excp.Message}");
    exitCode = 1;
}

Console.WriteLine(summary.ToString());
if (jsonOut != null)
{
    File.WriteAllText(jsonOut, results.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
}
return exitCode;

// --- Metric 1: LLM reply latency -------------------------------------------------------------

async Task RunAsk()
{
    await http.PostAsync($"{target}/bench/reset", null);

    var clientMs = new List<double>();
    foreach (var prompt in Enumerable.Range(0, askIterations).Select(i => prompts[i % prompts.Length]))
    {
        var sw = Stopwatch.StartNew();
        var resp = await http.PostAsync($"{target}/ask", new StringContent(prompt));
        resp.EnsureSuccessStatusCode();
        var reply = await resp.Content.ReadAsStringAsync();
        // /ask returns once the reply text is assembled (speech continues in the
        // background), so this is client-observed full-reply latency.
        clientMs.Add(sw.Elapsed.TotalMilliseconds);
        Console.Error.WriteLine($"ask ({sw.ElapsedMilliseconds} ms): {prompt} -> {Truncate(reply, 80)}");
        // Let queued speech drain so TTS/lip-sync timings attach to the right utterance
        // and consecutive prompts don't contend for the speak lock.
        await Task.Delay(TimeSpan.FromSeconds(8));
    }

    var serverAgg = await FetchAggregates();
    summary.AppendLine("## LLM reply latency");
    summary.AppendLine();
    summary.AppendLine($"{askIterations} prompts, client-observed `/ask` round trip (full reply):");
    summary.AppendLine();
    summary.AppendLine(StatsRow("client_ask_roundtrip", clientMs));
    foreach (var stage in new[] { "llm_first_sentence", "llm_stream_complete", "tts_synth" })
    {
        if (serverAgg?[stage] is JsonObject o) { summary.AppendLine(AggRow(stage, o)); }
    }
    summary.AppendLine();

    results["ask"] = new JsonObject
    {
        ["iterations"] = askIterations,
        ["client_ms"] = new JsonArray(clientMs.Select(v => JsonValue.Create(Math.Round(v, 1))).ToArray()),
        ["server_aggregates"] = serverAgg?.DeepClone(),
    };
}

// --- Metric 2: STT accuracy (WER) ------------------------------------------------------------

async Task RunStt()
{
    var manifestPath = FindFile("bench/corpus.json")
        ?? throw new FileNotFoundException("bench/corpus.json not found (run from the repo root)");
    var corpusDir = Path.GetDirectoryName(manifestPath)!;
    var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsArray();

    int totalWords = 0, totalErrors = 0;
    var perClip = new JsonArray();
    summary.AppendLine("## STT accuracy");
    summary.AppendLine();
    summary.AppendLine("| clip | words | WER | transcript (first 60 chars) |");
    summary.AppendLine("|---|---|---|---|");

    foreach (var item in manifest)
    {
        string name = item!["name"]!.GetValue<string>();
        string reference = item["transcript"]!.GetValue<string>();
        byte[] wav = await LoadClip(item, corpusDir);

        var resp = await http.PostAsync($"{target}/bench/stt", new ByteArrayContent(wav));
        resp.EnsureSuccessStatusCode();
        var stt = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!;
        string transcript = stt["transcript"]!.GetValue<string>();

        var refWords = Normalize(reference);
        var hypWords = Normalize(transcript);
        int errors = Levenshtein(refWords, hypWords);
        totalWords += refWords.Length;
        totalErrors += errors;
        double wer = refWords.Length == 0 ? 0 : (double)errors / refWords.Length;

        summary.AppendLine($"| {name} | {refWords.Length} | {wer:P1} | {Truncate(transcript, 60)} |");
        perClip.Add(new JsonObject
        {
            ["name"] = name,
            ["wer"] = Math.Round(wer, 4),
            ["words"] = refWords.Length,
            ["errors"] = errors,
            ["stt_ms"] = stt["ms"]?.DeepClone(),
            ["audio_ms"] = stt["audioMs"]?.DeepClone(),
            ["transcript"] = transcript,
        });
    }

    double overall = totalWords == 0 ? 0 : (double)totalErrors / totalWords;
    summary.AppendLine();
    summary.AppendLine($"**Overall WER: {overall:P1}** ({totalErrors} errors / {totalWords} words)");
    summary.AppendLine();
    results["stt"] = new JsonObject { ["overall_wer"] = Math.Round(overall, 4), ["clips"] = perClip };
}

// --- Metric 3 + everything else the server recorded ------------------------------------------

async Task RunReport()
{
    var agg = await FetchAggregates();
    summary.AppendLine("## Server stage timings (`/bench/metrics`)");
    summary.AppendLine();
    if (agg == null || agg.Count == 0)
    {
        summary.AppendLine("_No stage timings recorded (lip-sync stages need a connected WebRTC viewer while speaking)._");
    }
    else
    {
        summary.AppendLine("| stage | count | mean | p50 | p95 | max |");
        summary.AppendLine("|---|---|---|---|---|---|");
        foreach (var (name, node) in agg.OrderBy(kv => kv.Key))
        {
            if (node is JsonObject o)
            {
                summary.AppendLine($"| {name} | {o["count"]} | {o["mean"]} | {o["p50"]} | {o["p95"]} | {o["max"]} |");
            }
        }
    }
    summary.AppendLine();
    results["server_metrics"] = agg?.DeepClone();
}

// --- helpers ----------------------------------------------------------------------------------

async Task<JsonObject> FetchAggregates()
{
    var resp = await http.GetAsync($"{target}/bench/metrics");
    if (!resp.IsSuccessStatusCode) { return null; }
    return JsonNode.Parse(await resp.Content.ReadAsStringAsync())?["aggregates"] as JsonObject;
}

async Task<byte[]> LoadClip(JsonNode item, string corpusDir)
{
    string name = item["name"]!.GetValue<string>();
    string cached = Path.Combine(corpusDir, "cache", name);
    if (File.Exists(cached)) { return await File.ReadAllBytesAsync(cached); }

    if (item["file"] is JsonNode f)
    {
        return await File.ReadAllBytesAsync(Path.Combine(corpusDir, f.GetValue<string>()));
    }

    string url = item["url"]!.GetValue<string>();
    var wav = await http.GetByteArrayAsync(url);
    if (item["sha256"] is JsonNode s)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(wav)).ToLowerInvariant();
        if (hash != s.GetValue<string>().ToLowerInvariant())
        {
            throw new InvalidOperationException($"{name}: sha256 mismatch (got {hash})");
        }
    }
    Directory.CreateDirectory(Path.Combine(corpusDir, "cache"));
    await File.WriteAllBytesAsync(cached, wav);
    return wav;
}

static string[] Normalize(string text) =>
    new string(text.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) || c == '\'' ? c : ' ').ToArray())
        .Split(' ', StringSplitOptions.RemoveEmptyEntries);

// Word-level edit distance (substitutions + insertions + deletions).
static int Levenshtein(string[] a, string[] b)
{
    var prev = Enumerable.Range(0, b.Length + 1).ToArray();
    var curr = new int[b.Length + 1];
    for (int i = 1; i <= a.Length; i++)
    {
        curr[0] = i;
        for (int j = 1; j <= b.Length; j++)
        {
            int cost = a[i - 1] == b[j - 1] ? 0 : 1;
            curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
        }
        (prev, curr) = (curr, prev);
    }
    return prev[b.Length];
}

static string StatsRow(string name, List<double> values)
{
    var sorted = values.OrderBy(v => v).ToArray();
    double P(double p) { var r = p * (sorted.Length - 1); int lo = (int)r; return lo + 1 < sorted.Length ? sorted[lo] + (r - lo) * (sorted[lo + 1] - sorted[lo]) : sorted[lo]; }
    return $"- **{name}**: mean {sorted.Average():F0} ms, p50 {P(0.5):F0} ms, p95 {P(0.95):F0} ms ({sorted.Length} samples)";
}

static string AggRow(string name, JsonObject o) =>
    $"- **{name}** (server): mean {o["mean"]} ms, p50 {o["p50"]} ms, p95 {o["p95"]} ms ({o["count"]} samples)";

static string Truncate(string s, int len) => s.Length <= len ? s : s[..len] + "…";

string GetOpt(string name)
{
    int idx = Array.IndexOf(args, name);
    return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
}

static string FindFile(string relative)
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir != null)
    {
        var candidate = Path.Combine(dir.FullName, relative);
        if (File.Exists(candidate)) { return candidate; }
        dir = dir.Parent;
    }
    return null;
}
