export function firstSustainedCrossing(samples, threshold, consecutive = 3, notBefore = 0) {
  let run = 0;
  for (let index = 0; index < samples.length; index += 1) {
    const sample = samples[index];
    if (sample.timeMs >= notBefore && sample.value >= threshold) {
      run += 1;
      if (run >= consecutive) return samples[index - consecutive + 1].timeMs;
    } else {
      run = 0;
    }
  }
  return null;
}

export function meanAbsolutePixelDifference(before, after) {
  if (before.length !== after.length || before.length === 0) {
    throw new Error("Pixel buffers must be non-empty and equally sized.");
  }
  let sum = 0;
  let channels = 0;
  for (let i = 0; i < before.length; i += 4) {
    sum += Math.abs(before[i] - after[i]);
    sum += Math.abs(before[i + 1] - after[i + 1]);
    sum += Math.abs(before[i + 2] - after[i + 2]);
    channels += 3;
  }
  return sum / channels;
}

export function buildCase(observation, options = {}) {
  const audioThreshold = options.audioThreshold ?? 0.018;
  const mouthThreshold = options.mouthThreshold ?? 4;
  const speechEnd = observation.speechEndMs;
  const audioOnset = firstSustainedCrossing(observation.audioSamples, audioThreshold, 3, speechEnd);
  const mouthOnset = firstSustainedCrossing(observation.mouthSamples, mouthThreshold, 2, speechEnd);
  const events = observation.server?.events ?? [];
  const speechEndServer = events.find(x => x.name === "speech_end")?.elapsedMilliseconds;

  const metricsMilliseconds = {};
  for (const name of ["stt_final", "llm_first_token", "audio_started", "first_mouth_frame"]) {
    const event = events.find(x => x.name === name);
    if (event && speechEndServer != null && event.elapsedMilliseconds >= speechEndServer) {
      metricsMilliseconds[`server_speech_end_to_${name}_ms`] = event.elapsedMilliseconds - speechEndServer;
    }
  }
  if (audioOnset != null) metricsMilliseconds.client_speech_end_to_audio_ms = audioOnset - speechEnd;
  if (mouthOnset != null) metricsMilliseconds.client_speech_end_to_first_mouth_ms = mouthOnset - speechEnd;

  const renderer = observation.server?.renderer;
  if (renderer) {
    metricsMilliseconds.wav2lip_mean_inference_ms = renderer.meanInferenceMilliseconds;
    metricsMilliseconds.wav2lip_max_inference_ms = renderer.maximumInferenceMilliseconds;
    metricsMilliseconds.video_mean_encode_ms = renderer.meanEncodeMilliseconds;
    metricsMilliseconds.video_max_encode_ms = renderer.maximumEncodeMilliseconds;
    metricsMilliseconds.mouth_frame_mean_lateness_ms = renderer.meanMouthFrameLatenessMilliseconds;
    metricsMilliseconds.mouth_frame_max_lateness_ms = renderer.maximumMouthFrameLatenessMilliseconds;
  }

  const metrics = {};
  if (audioOnset != null && mouthOnset != null) metrics.received_mouth_minus_audio_ms = mouthOnset - audioOnset;
  if (renderer) {
    metrics.effective_fps = renderer.effectiveFramesPerSecond;
    metrics.dropped_ticks = renderer.droppedTicks;
  }

  const required = ["stt_final", "llm_first_token", "audio_started", "first_mouth_frame"];
  const missing = required.filter(name => !events.some(x => x.name === name));
  if (audioOnset == null) missing.push("received_audio_onset");
  if (mouthOnset == null) missing.push("received_mouth_onset");

  return {
    name: "deterministic_speech_transition",
    correlationId: observation.sessionId,
    iteration: 0,
    warm: true,
    succeeded: missing.length === 0,
    metricsMilliseconds,
    metrics,
    events,
    ...(missing.length ? { error: `Missing: ${missing.join(", ")}` } : {}),
  };
}

export function summarize(values) {
  if (!values.length) return { count: 0 };
  const sorted = [...values].sort((a, b) => a - b);
  const percentile = p => {
    const rank = (p / 100) * (sorted.length - 1);
    const low = Math.floor(rank);
    const high = Math.ceil(rank);
    return sorted[low] + (sorted[high] - sorted[low]) * (rank - low);
  };
  return {
    count: sorted.length,
    minimum: sorted[0],
    maximum: sorted.at(-1),
    mean: sorted.reduce((a, b) => a + b, 0) / sorted.length,
    p50: percentile(50),
    p90: percentile(90),
    p95: percentile(95),
  };
}
