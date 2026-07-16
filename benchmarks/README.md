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
