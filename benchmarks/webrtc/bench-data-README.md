# Max benchmark data

This branch is the single published record for Max performance measurements.
The [`bench` workflow](https://github.com/sipsorcery/maxheadroom/blob/master/.github/workflows/bench.yml)
runs two complementary suites and regenerates [history.md](history.md) plus its
SVG charts after each successful pass. Raw JSON is retained under `runs/`.

## What the two suites measure

| Suite | Method | Best for |
|---|---|---|
| HTTP/API | A .NET client sends a fixed prompt set to `/ask`, posts reference clips to `/bench/stt`, and reads server timing endpoints. | Repeatable server and model-stage trends, including STT word error rate. |
| Browser WebRTC | Headless Chrome injects deterministic WAV speech into a real WebRTC microphone connection and observes returned audio and video. | The viewer-visible path: STT, LLM, audible reply, mouth motion, A/V offset, and renderer health. |

The suites use different start points. HTTP/API latency starts when its `/ask`
request is posted; browser/WebRTC latency starts at the injected speech's
end-of-speech boundary. Their tables and charts are therefore intentionally
separate — use each to compare like with like, not to rank one number against
the other.

## Running locally

See [`bench/README.md`](https://github.com/sipsorcery/maxheadroom/blob/master/bench/README.md)
for the HTTP/API suite. The browser suite needs Chrome and uses
`benchmarks/webrtc/run.mjs`; it takes a WAV/FLAC prompt containing deterministic
speech followed by silence. The workflow downloads the public Open Speech
Repository Harvard-sentence clip, keeps its first sentence, and appends 800 ms
of silence before running three transitions.

The browser suite's mouth detector looks for motion in a fixed face region. A
browser A/V result whose sample spread exceeds 1,500 ms is labelled provisional;
inspect the raw JSON and diagnostic artifact before treating it as a regression.
