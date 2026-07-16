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
