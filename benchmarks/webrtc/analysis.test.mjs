import test from "node:test";
import assert from "node:assert/strict";
import { buildCase, firstSustainedCrossing, meanAbsolutePixelDifference, relativeRegionMotion, summarize } from "./analysis.mjs";

test("requires a sustained threshold crossing", () => {
  const samples = [0, 8, 1, 8, 9].map((value, i) => ({ timeMs: i * 10, value }));
  assert.equal(firstSustainedCrossing(samples, 7, 2), 30);
});

test("calculates RGB difference without alpha", () => {
  assert.equal(meanAbsolutePixelDifference(new Uint8Array([0, 10, 20, 1]), new Uint8Array([3, 16, 29, 255])), 6);
});

test("subtracts shared head motion from mouth motion", () => {
  const still = new Uint8Array([10, 10, 10, 255]);
  const moved = new Uint8Array([14, 14, 14, 255]);
  const talking = new Uint8Array([20, 20, 20, 255]);
  assert.equal(relativeRegionMotion(still, talking, still, moved), 6);
});

test("reports signed received mouth to audio offset", () => {
  const observation = {
    sessionId: "case-1",
    speechEndMs: 100,
    audioSamples: [100, 110, 120].map(timeMs => ({ timeMs, value: 0.1 })),
    mouthSamples: [100, 110].map(timeMs => ({ timeMs, value: 10 })),
    server: {
      events: [
        { name: "speech_end", elapsedMilliseconds: 20 },
        { name: "stt_final", elapsedMilliseconds: 30 },
        { name: "llm_first_token", elapsedMilliseconds: 40 },
        { name: "audio_started", elapsedMilliseconds: 50 },
        { name: "audio_complete", elapsedMilliseconds: 80 },
        { name: "first_mouth_frame", elapsedMilliseconds: 55 },
      ],
      receivedAudio: { frames: 192, samples: 30720, rms: 4200, peak: 15000 },
    },
  };
  const result = buildCase(observation);
  assert.equal(result.succeeded, true);
  assert.equal(result.metrics.received_mouth_minus_audio_ms, 0);
  assert.equal(result.metricsMilliseconds.server_speech_end_to_stt_final_ms, 10);
  assert.deepEqual(result.serverReceivedAudio, { frames: 192, samples: 30720, rms: 4200, peak: 15000 });
  assert.equal(result.metrics.server_received_audio_rms, 4200);
});

test("summarizes interpolated percentiles", () => {
  assert.deepEqual(summarize([1, 3]), { count: 2, minimum: 1, maximum: 3, mean: 2, p50: 2, p90: 2.8, p95: 2.9 });
});
