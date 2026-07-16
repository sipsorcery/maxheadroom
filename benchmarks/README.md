# Max performance benchmarks

Benchmark output is versioned JSON built from the types under
`src/AvatarPipeline/Performance`. Version 1 records source and environment
metadata, correlated cases, monotonic pipeline events, millisecond metrics, and
aggregate p50/p90/p95 summaries.

## Measurement rules

- Durations use a monotonic `TimeProvider`; UTC is metadata only.
- Latency metrics are milliseconds and use lower-case snake-case names.
- Cold and warm cases are separate samples.
- Failed cases remain in the result with `succeeded: false` and a sanitized
  error. API keys, authorization headers, and other secrets must never appear.
- Empty summaries serialize with `count: 0` and omit nullable statistics rather
  than emitting non-standard `NaN` values.

The first stable event names cover the full spoken-response path from
`speech_end` through STT, LLM, TTS, audio and the first correlated mouth frame.
Individual benchmark issues will add the instrumentation and runners that
populate this contract.

## LLM and first-audio benchmark

Set `BENCHMARK_ENDPOINT_ENABLED=true` on a development instance and connect a
browser viewer. `POST /benchmark/llm` then runs the fixed `llm-latency-v1`
prompt suite and returns a schema-v1 result. The endpoint is deliberately
opt-in and requires an active viewer because `tts_audio_ready` and
`audio_started` describe the real avatar audio path.

The LLM metrics are measured from `prompt_accepted`: request start, response
headers (HTTP streaming clients), first content token, first sentence, and
completion. In-process LLMs do not have an HTTP headers boundary, so that
metric is omitted rather than fabricated. TTS implementations report the
first synthesized audio and first audio handoff to the WebRTC source.

## STT accuracy and latency benchmark

`stt/mini-librispeech-v1.json` pins the Mini LibriSpeech SLR31 regression corpus
to its OpenSLR archive URL and MD5. The source page documents the corpus as CC
BY 4.0. The archive is downloaded by the benchmark environment and is not
committed to this repository. The deterministic selector uses at least four
speakers, reads the expected `.trans.txt` references, and caps the run at 32
utterances.

Each selected recording is evaluated once clean and once with the manifest's
20 dB additive-noise transform. Results report per-utterance and aggregate
WER, CER, engine latency, end-of-speech-to-final latency, and real-time factor.
The existing TTS-to-STT round trip remains a smoke test; it is not used as the
accuracy corpus.

## Browser-observed WebRTC lip-sync benchmark

The `webrtc` runner uses headless Chromium as a real WebRTC peer. It sends a fixed
speech recording on a synthetic microphone track, receives Max's audio and video,
and measures response-audio onset plus the first sustained inter-frame change in
the mouth region.
The signed `received_mouth_minus_audio_ms` value is positive when the mouth arrives
after the audio and negative when it leads.

The server benchmark API is opt-in with `BENCHMARK_ENDPOINT_ENABLED=true`. The
client creates a session before SDP exchange, waits for Max's connection greeting
to finish, then arms the session. Server events decompose end-of-speech through STT,
LLM, audio handoff and the first generated mouth frame. Wav2Lip inference, encode,
effective-FPS, dropped-tick and mouth-frame-lateness counters are observational and
do not alter RTP timestamps or media payloads.

```bash
cd benchmarks/webrtc
npm install
npx playwright install chromium
node run.mjs --url https://max.sipsorcery.com --audio ./prompt.wav
```

The input must end with 600 ms of silence (override with
`--trailing-silence-ms`). That fixed transition lets the client mark speech end
before VAD produces its final transcript.

Results are schema-v1 JSON. A failed run also retains a short WebM diagnostic
recording; successful runs discard it.
