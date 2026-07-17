# Max avatar performance bench

Measures three things against a running Max instance:

| Metric | How |
|---|---|
| **LLM reply latency** | `MaxBench ask` POSTs a fixed prompt set to `/ask` and reports the client round trip plus server stage timings: `llm_first_sentence` (prompt → first spoken sentence available) and `llm_stream_complete`. |
| **STT accuracy** | `MaxBench stt` sends the clips in `corpus.json` to `/bench/stt` (same offline recogniser the live WebRTC audio path uses) and scores word error rate against the reference transcripts. |
| **Lip-sync latency** | Server-side stages surfaced by `MaxBench report`: `lipsync_first_mouth` (speech start → first lip-synced mouth frame ready) and `wav2lip_infer` (per-window ONNX inference). These record whenever the avatar speaks with a connected viewer. |

## Prerequisites

The target instance must run with `BENCH_ENDPOINTS=true` or
`BENCHMARK_ENDPOINT_ENABLED=true` (**not** enabled on production), which exposes:

- `GET /bench/metrics` — stage-timing ring buffer + aggregates
- `POST /bench/reset` — clear the buffer before a measurement pass
- `POST /bench/stt` — WAV in (16-bit PCM), transcript + timing out
- `/benchmark/webrtc/*` — correlated browser/WebRTC session events and media diagnostics

## Running

```bash
dotnet run --project src/MaxBench -c Release -- all --target https://max-claude.sipsorcery.com --json bench-results.json
```

Modes: `ask` (default 5 iterations, `--iterations N`), `stt`, `report`, `all`.
Markdown summary goes to stdout; `--json` writes machine-readable results.

CI: `.github/workflows/bench.yml` runs nightly or by deliberate manual dispatch.
Both the HTTP/API suite and the browser/WebRTC suite always measure the same
`target`, sequentially, so a result describes one deployed experiment without
competing WebRTC viewers. To compare Codex and Claude, dispatch one run for each
deployment URL. The target's `/version` branch and SHA identify the experiment;
the optional `deployment` input can override the branch label. Feature-branch
pushes do not automatically spend runner time.

## Trend history

Every CI run appends its results to the **[`bench-data` branch](../../../tree/bench-data)**:
one JSON per run under `runs/`, plus a regenerated
**[history.md](../../../blob/bench-data/history.md)** with a most-recent-first
table and SVG trend charts (latency p50s and WER over time). Regenerate locally
with `dotnet run --project src/MaxBench -- history --history-dir <dir>`.

## Corpus

`corpus.json` lists reference clips (URL + sha256 + transcript). Current corpus
is Harvard sentence list 1 from the public-domain Open Speech Repository
(8 kHz — deliberately matches the 8 kHz PCM the live WebRTC decode path feeds
the recogniser). Add `{"name", "file"|"url", "sha256", "transcript"}` entries
to grow it; downloads are cached under `bench/cache/`.
