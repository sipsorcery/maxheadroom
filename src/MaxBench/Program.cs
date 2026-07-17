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
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

var mode = args.Length > 0 ? args[0] : "all";
string target = GetOpt("--target") ?? Environment.GetEnvironmentVariable("BENCH_TARGET") ?? "http://localhost:8080";
string jsonOut = GetOpt("--json");
int askIterations = int.TryParse(GetOpt("--iterations"), out var it) ? it : 5;
target = target.TrimEnd('/');

// history mode is offline: regenerate the trend table + charts from stored run JSONs.
if (mode == "history")
{
    demo.bench.History.Generate(GetOpt("--history-dir") ?? "bench-history");
    return 0;
}

var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
var results = new JsonObject { ["target"] = target, ["utc"] = DateTime.UtcNow.ToString("o"), ["mode"] = mode };
if (GetOpt("--label") is string label) { results["label"] = label; }

// The LLM/TTS/STT/renderer in play is config, not code - a model or voice swap doesn't
// change the git sha, so without this the history can't tell two runs apart under the
// same label. Best-effort: an old/unbadged instance just won't have the fields.
string llmModel = null;
try
{
    var versionResp = await http.GetAsync($"{target}/version");
    if (versionResp.IsSuccessStatusCode)
    {
        var version = JsonNode.Parse(await versionResp.Content.ReadAsStringAsync());
        llmModel = version?["llmModel"]?.GetValue<string>();
        if (llmModel != null) { results["llm_model"] = llmModel; }
        if (version?["models"] is JsonNode models) { results["models"] = models.DeepClone(); }
    }
}
catch { /* best effort */ }

var summary = new StringBuilder();
summary.AppendLine($"# Max bench — {target}");
if (llmModel != null) { summary.AppendLine($"LLM: `{llmModel}`"); }
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
if (mode is "ask" or "all") { await RunMode("ask", RunAsk); }
if (mode is "stt" or "all") { await RunMode("stt", RunStt); }
if (mode is "report" or "all") { await RunMode("report", RunReport); }

async Task RunMode(string name, Func<Task> run)
{
    try
    {
        await run();
    }
    catch (Exception excp)
    {
        summary.AppendLine($"**{name} FAILED**: {excp.Message}");
        summary.AppendLine();
        exitCode = 1;
    }
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
    // The avatar's speaker pipeline only exists while a WebRTC session is up, so
    // /ask 400s without one. Connect a receive-only viewer (the same SIPSorcery
    // stack Max serves with) and keep it open for the whole measurement pass. It
    // also gives us the truest latency of all: prompt POST -> first audible RTP.
    var viewer = await BenchViewer.ConnectAsync(target);

    await http.PostAsync($"{target}/bench/reset", null);

    var clientMs = new List<double>();
    var firstAudioMs = new List<double>();
    int failures = 0;
    try
    {
        foreach (var prompt in Enumerable.Range(0, askIterations).Select(i => prompts[i % prompts.Length]))
        {
            try
            {
                viewer.ArmAudioLatch();
                long t0 = Environment.TickCount64;
                var sw = Stopwatch.StartNew();
                var resp = await http.PostAsync($"{target}/ask", new StringContent(prompt));

                if (resp.StatusCode == System.Net.HttpStatusCode.BadRequest)
                {
                    // The server drives a single viewer; another connection (or a drop)
                    // replaces the speaker and our session with it. Reconnect once and
                    // retry this prompt.
                    Console.Error.WriteLine("ask: session lost (400) - reconnecting viewer");
                    await viewer.DisposeAsync();
                    viewer = await BenchViewer.ConnectAsync(target);
                    viewer.ArmAudioLatch();
                    t0 = Environment.TickCount64;
                    sw.Restart();
                    resp = await http.PostAsync($"{target}/ask", new StringContent(prompt));
                }
                resp.EnsureSuccessStatusCode();
                var reply = await resp.Content.ReadAsStringAsync();
                // /ask returns once the reply text is assembled (speech continues in the
                // background), so this is client-observed full-reply latency.
                clientMs.Add(sw.Elapsed.TotalMilliseconds);

                long? audioAt = await viewer.WaitForAudibleAudioAsync(TimeSpan.FromSeconds(30));
                if (audioAt != null) { firstAudioMs.Add(audioAt.Value - t0); }
                Console.Error.WriteLine($"ask ({sw.ElapsedMilliseconds} ms, first audio {(audioAt != null ? $"{audioAt.Value - t0} ms" : "none")}): {Truncate(prompt, 40)} -> {Truncate(reply, 60)}");

                // Drain the utterance fully (wait for sustained silence) so the next prompt's
                // first-audio can't latch onto this reply's tail and TTS/lip-sync timings
                // attach to the right utterance. No audio at all -> nothing to drain.
                // The quiet window must exceed the inter-sentence synthesis gap (tts_synth
                // p95 ~4s): a shorter window mistakes "synthesising the next sentence" for
                // end-of-reply and the viewer then disconnects mid-speech - which currently
                // segfaults the server (see the teardown-race issue).
                if (audioAt != null)
                {
                    await viewer.WaitForSilenceAsync(TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(120));
                }
            }
            catch (Exception excp)
            {
                failures++;
                Console.Error.WriteLine($"ask: prompt failed ({excp.Message}); continuing");
            }
        }
    }
    finally
    {
        await viewer.DisposeAsync();
    }

    var serverAgg = await FetchAggregates();
    summary.AppendLine("## LLM reply latency");
    summary.AppendLine();
    summary.AppendLine($"{askIterations} prompts against a live WebRTC viewer:");
    summary.AppendLine();
    if (firstAudioMs.Count > 0)
    {
        summary.AppendLine(StatsRow("prompt_to_first_audio (end-to-end)", firstAudioMs));
    }
    if (clientMs.Count > 0)
    {
        summary.AppendLine(StatsRow("client_ask_roundtrip (full reply text)", clientMs));
    }
    foreach (var stage in new[] { "llm_first_sentence", "llm_stream_complete", "tts_synth" })
    {
        if (serverAgg?[stage] is JsonObject o) { summary.AppendLine(AggRow(stage, o)); }
    }
    summary.AppendLine();

    if (failures > 0)
    {
        summary.AppendLine($"_({failures} of {askIterations} prompts failed and were skipped.)_");
        summary.AppendLine();
    }

    results["ask"] = new JsonObject
    {
        ["iterations"] = askIterations,
        ["failures"] = failures,
        ["client_ms"] = new JsonArray(clientMs.Select(v => JsonValue.Create(Math.Round(v, 1))).ToArray()),
        ["first_audio_ms"] = new JsonArray(firstAudioMs.Select(v => JsonValue.Create(Math.Round(v, 1))).ToArray()),
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

/// <summary>
/// Receive-only WebRTC viewer. Holding a session open makes the server build its speaker
/// pipeline (a prerequisite for /ask and /say), and watching inbound audio RTP for the
/// first audible packet gives an end-to-end prompt-to-first-audio latency. Offers G.711
/// so audio can be energy-checked without an Opus decoder.
/// </summary>
sealed class BenchViewer : IAsyncDisposable
{
    private readonly RTCPeerConnection _pc;
    private volatile TaskCompletionSource<long> _audioLatch = new();

    private BenchViewer(RTCPeerConnection pc) => _pc = pc;

    public static async Task<BenchViewer> ConnectAsync(string target)
    {
        var pc = new RTCPeerConnection(new RTCConfiguration
        {
            iceServers = [new RTCIceServer { urls = "stun:stun.cloudflare.com" }],
        });

        var audio = new MediaStreamTrack(
            new List<AudioFormat> { new(SDPWellKnownMediaFormatsEnum.PCMU), new(SDPWellKnownMediaFormatsEnum.PCMA) },
            MediaStreamStatusEnum.RecvOnly);
        var video = new MediaStreamTrack(
            new List<VideoFormat> { new(VideoCodecsEnum.VP8, 96), new(VideoCodecsEnum.H264, 100) },
            MediaStreamStatusEnum.RecvOnly);
        pc.addTrack(audio);
        pc.addTrack(video);

        var viewer = new BenchViewer(pc);
        pc.OnRtpPacketReceived += viewer.OnRtp;

        var connected = new TaskCompletionSource();
        pc.onconnectionstatechange += state =>
        {
            if (state == RTCPeerConnectionState.connected) { connected.TrySetResult(); }
            else if (state is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed)
            {
                connected.TrySetException(new InvalidOperationException($"WebRTC connection {state}"));
            }
        };

        var offer = pc.createOffer();
        await pc.setLocalDescription(offer);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var resp = await http.PostAsync($"{target}/offer", new StringContent(pc.localDescription.sdp.ToString()));
        resp.EnsureSuccessStatusCode();
        var answerSdp = await resp.Content.ReadAsStringAsync();
        var setResult = pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = answerSdp });
        if (setResult != SetDescriptionResultEnum.OK)
        {
            throw new InvalidOperationException($"setRemoteDescription failed: {setResult}");
        }

        await connected.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Console.Error.WriteLine("bench viewer: WebRTC connected");
        return viewer;
    }

    /// <summary>Re-arms the audible-audio latch; the next audible RTP packet completes it.</summary>
    public void ArmAudioLatch() => _audioLatch = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Returns Environment.TickCount64 at the first audible audio packet, or null on timeout.</summary>
    public async Task<long?> WaitForAudibleAudioAsync(TimeSpan timeout)
    {
        try { return await _audioLatch.Task.WaitAsync(timeout); }
        catch (TimeoutException) { return null; }
    }

    /// <summary>Waits until no audible audio has arrived for <paramref name="quiet"/> (i.e. the
    /// current utterance has fully drained), or gives up after <paramref name="timeout"/>.</summary>
    public async Task WaitForSilenceAsync(TimeSpan quiet, TimeSpan timeout)
    {
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            long last = Interlocked.Read(ref _lastAudibleAt);
            if (last > 0 && Environment.TickCount64 - last >= quiet.TotalMilliseconds)
            {
                return;
            }
            await Task.Delay(250);
        }
    }

    private long _lastAudibleAt;

    private void OnRtp(System.Net.IPEndPoint remote, SDPMediaTypesEnum media, RTPPacket packet)
    {
        if (media != SDPMediaTypesEnum.audio)
        {
            return;
        }

        // G.711 silence sits near the zero codes; treat a frame as audible when its mean
        // decoded amplitude clears a floor comfortably above comfort noise.
        var payload = packet.Payload;
        if (payload == null || payload.Length == 0) { return; }
        bool alaw = packet.Header.PayloadType == 8;
        long sum = 0;
        foreach (var b in payload)
        {
            sum += Math.Abs((int)(alaw ? ALawDecode(b) : MuLawDecode(b)));
        }
        if (sum / payload.Length > 200)
        {
            Interlocked.Exchange(ref _lastAudibleAt, Environment.TickCount64);
            _audioLatch.TrySetResult(Environment.TickCount64);
        }
    }

    private static short MuLawDecode(byte b)
    {
        b = (byte)~b;
        int sign = b & 0x80;
        int exponent = (b >> 4) & 0x07;
        int mantissa = b & 0x0F;
        int sample = ((mantissa << 3) + 0x84) << exponent;
        sample -= 0x84;
        return (short)(sign != 0 ? -sample : sample);
    }

    private static short ALawDecode(byte b)
    {
        b ^= 0x55;
        int sign = b & 0x80;
        int exponent = (b >> 4) & 0x07;
        int mantissa = b & 0x0F;
        int sample = exponent == 0 ? (mantissa << 4) + 8 : ((mantissa << 4) + 0x108) << (exponent - 1);
        return (short)(sign != 0 ? -sample : sample);
    }

    public async ValueTask DisposeAsync()
    {
        try { _pc.close(); } catch { /* best effort */ }
        await Task.CompletedTask;
    }
}
