import assert from "node:assert/strict";
import test from "node:test";
import {
  buildHistory,
  buildLatencySvg,
  buildPipelineSvg,
  combineResults,
  describeResult,
} from "./report.mjs";

function sample(iteration, values = {}) {
  const metricsMilliseconds = {
    server_speech_end_to_stt_final_ms: 1000 + iteration * 100,
    server_speech_end_to_llm_first_token_ms: 1300 + iteration * 100,
    server_speech_end_to_audio_started_ms: 3300 + iteration * 100,
    server_speech_end_to_first_mouth_frame_ms: 3700 + iteration * 100,
    client_speech_end_to_audio_ms: 3400 + iteration * 100,
    client_speech_end_to_first_mouth_ms: 3850 + iteration * 100,
    wav2lip_mean_inference_ms: 70 + iteration,
    video_mean_encode_ms: 5 + iteration,
    ...values.metricsMilliseconds,
  };
  return {
    schemaVersion: 1,
    runId: `run-${iteration}`,
    startedAtUtc: `2026-07-16T22:2${iteration}:00Z`,
    source: {},
    environment: {
      machineName: "runner",
      operatingSystem: "test",
      processArchitecture: "x64",
      framework: "Node",
      processorCount: 2,
    },
    configuration: {
      suite: "webrtc-lipsync-v1",
      target: "https://example.test",
      browserChannel: "chrome",
    },
    cases: [{
      name: "case",
      correlationId: `case-${iteration}`,
      iteration: 0,
      warm: true,
      succeeded: true,
      metricsMilliseconds,
      metrics: {
        received_mouth_minus_audio_ms: values.browserOffset ?? 450 + iteration * 100,
        effective_fps: 20 - iteration,
        dropped_ticks: 10 + iteration,
      },
      events: [],
    }],
    summaries: {},
  };
}

test("combines samples and records reproducibility metadata", () => {
  const combined = combineResults([sample(0), sample(1), sample(2)], {
    runId: "batch",
    commitSha: "519c7c76143fa315e1dc3361a0d7a406cd79da49",
    imageDigest: "sha256:abc",
    target: "https://max-codex.example",
    deployment: "codex",
    runner: "test-runner",
    llmModel: "mistral-test",
    deploymentRestarts: 0,
  });

  assert.equal(combined.cases.length, 3);
  assert.deepEqual(combined.cases.map(item => item.iteration), [0, 1, 2]);
  assert.equal(combined.summaries.server_speech_end_to_stt_final_ms.p50, 1100);
  assert.equal(combined.source.commitSha, "519c7c76143fa315e1dc3361a0d7a406cd79da49");
  assert.equal(combined.configuration.deploymentRestarts, "0");
  assert.equal(combined.configuration.llmModel, "mistral-test");
  assert.equal(combined.environment.machineName, "test-runner");
  assert.equal(combined.environment.framework, "Node");
});

test("history separates stage latency and flags unstable browser motion", () => {
  const combined = combineResults([
    sample(0, { browserOffset: 350 }),
    sample(1, { browserOffset: 1000 }),
    sample(2, { browserOffset: 4100 }),
  ], {
    runId: "batch",
    commitSha: "519c7c7",
    imageDigest: "sha256:abc",
    deployment: "codex",
    deploymentRestarts: 0,
  });
  const run = describeResult(combined, "batch.json");
  const markdown = buildHistory([run]);

  assert.equal(run.stages.llm, 300);
  assert.equal(run.stages.tts, 2000);
  assert.equal(run.stages.lipsync, 400);
  assert.match(markdown, /TTS to first audio.*2,000 ms p50/);
  assert.match(markdown, /⚠ provisional/);
  assert.match(markdown, /\[519c7c7\]\(runs\/batch\.json\)/);
});

test("charts are GitHub-renderable SVGs", () => {
  const run = describeResult(combineResults([sample(0)], {
    runId: "batch",
    commitSha: "519c7c7",
    imageDigest: "sha256:abc",
    deployment: "codex",
    deploymentRestarts: 0,
  }));

  assert.match(buildPipelineSvg(run), /^<svg/);
  assert.match(buildPipelineSvg(run), /TTS to first audio/);
  assert.match(buildLatencySvg([run]), /^<svg/);
  assert.match(buildLatencySvg([run]), /first audio \(e2e\)/);
});
