import { mkdir, readFile, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import process from "node:process";
import { chromium } from "playwright";
import { buildCase, summarize } from "./analysis.mjs";

function option(name, fallback) {
  const index = process.argv.indexOf(`--${name}`);
  return index >= 0 ? process.argv[index + 1] : fallback;
}

const baseUrl = option("url", "https://max.sipsorcery.com").replace(/\/$/, "");
const audioPath = option("audio");
const outputPath = option("out", "artifacts/webrtc-lipsync/result.json");
const recordingPath = option("recording", "artifacts/webrtc-lipsync/diagnostic.webm");
const timeoutMs = Number(option("timeout-ms", "45000"));
const trailingSilenceMs = Number(option("trailing-silence-ms", "600"));
const browserChannel = option("browser-channel", "chrome");
const commitSha = option("commit-sha", process.env.GITHUB_SHA);

if (!audioPath) {
  throw new Error("Pass --audio <wav-or-flac> containing deterministic speech followed by silence.");
}

const audioBase64 = (await readFile(audioPath)).toString("base64");
// Max currently sends H264 only. Playwright's bundled open-source Chromium build does not
// include H264 on every platform, while the installed Chrome channel does.
const browser = await chromium.launch({
  channel: browserChannel,
  headless: true,
  args: ["--autoplay-policy=no-user-gesture-required"],
});
let observation;

try {
  const context = await browser.newContext({ ignoreHTTPSErrors: true });
  const page = await context.newPage();
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });

  observation = await page.evaluate(async ({ baseUrl, audioBase64, timeoutMs, trailingSilenceMs }) => {
    const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
    const created = await fetch(`${baseUrl}/benchmark/webrtc/session`, { method: "POST" });
    if (!created.ok) throw new Error(`Benchmark endpoint unavailable (${created.status}).`);
    const { sessionId } = await created.json();

    const audioContext = new AudioContext({ sampleRate: 48000 });
    await audioContext.resume();
    const microphone = audioContext.createMediaStreamDestination();
    const bytes = Uint8Array.from(atob(audioBase64), c => c.charCodeAt(0));
    const injectedAudio = await audioContext.decodeAudioData(bytes.buffer.slice(0));

    const pc = new RTCPeerConnection({ iceServers: [{ urls: "stun:stun.cloudflare.com" }] });
    pc.addTrack(microphone.stream.getAudioTracks()[0], microphone.stream);
    pc.addTransceiver("video", { direction: "recvonly" });

    const remote = new MediaStream();
    pc.ontrack = event => remote.addTrack(event.track);
    const offer = await pc.createOffer();
    await pc.setLocalDescription(offer);
    const answer = await fetch(`${baseUrl}/offer`, {
      method: "POST",
      headers: { "Content-Type": "application/sdp", "X-Max-Benchmark-Session": sessionId },
      body: pc.localDescription.sdp,
    });
    if (!answer.ok) throw new Error(`Offer failed (${answer.status}).`);
    await pc.setRemoteDescription({ type: "answer", sdp: await answer.text() });

    const deadline = performance.now() + timeoutMs;
    while ((pc.connectionState !== "connected" || remote.getAudioTracks().length === 0 || remote.getVideoTracks().length === 0) && performance.now() < deadline) {
      await sleep(50);
    }
    if (pc.connectionState !== "connected") throw new Error(`WebRTC did not connect (${pc.connectionState}).`);

    const video = document.createElement("video");
    video.muted = true;
    video.playsInline = true;
    video.srcObject = remote;
    document.body.append(video);
    await video.play();

    const remoteAudio = audioContext.createMediaStreamSource(new MediaStream(remote.getAudioTracks()));
    const analyser = audioContext.createAnalyser();
    analyser.fftSize = 1024;
    remoteAudio.connect(analyser);
    const waveform = new Float32Array(analyser.fftSize);
    const currentRms = () => {
      analyser.getFloatTimeDomainData(waveform);
      let energy = 0;
      for (const value of waveform) energy += value * value;
      return Math.sqrt(energy / waveform.length);
    };

    // The app greets a new viewer. Arm only after 1.2 seconds of received silence so that
    // greeting cannot be mistaken for the benchmark response.
    let quietSince = performance.now();
    while (performance.now() < deadline) {
      if (currentRms() > 0.012) quietSince = performance.now();
      if (performance.now() - quietSince >= 1200) break;
      await sleep(25);
    }

    const canvas = document.createElement("canvas");
    canvas.width = 160;
    canvas.height = 120;
    const graphics = canvas.getContext("2d", { willReadFrequently: true });
    const roi = { x: 68, y: 76, width: 40, height: 28 };
    const controlRoi = { x: 68, y: 48, width: 40, height: 20 };
    graphics.drawImage(video, 0, 0, canvas.width, canvas.height);
    let previousMouth = graphics.getImageData(roi.x, roi.y, roi.width, roi.height).data.slice();
    let previousControl = graphics.getImageData(controlRoi.x, controlRoi.y, controlRoi.width, controlRoi.height).data.slice();

    const mimeType = ["video/webm;codecs=vp8,opus", "video/webm"].find(MediaRecorder.isTypeSupported) ?? "";
    const chunks = [];
    const recorder = new MediaRecorder(remote, mimeType ? { mimeType } : undefined);
    recorder.ondataavailable = event => {
      if (!event.data.size) return;
      chunks.push(event.data);
      if (chunks.length > 80) chunks.shift(); // retain roughly the final 20 seconds
    };
    recorder.start(250);

    const armed = await fetch(`${baseUrl}/benchmark/webrtc/${sessionId}/start`, { method: "POST" });
    if (!armed.ok) throw new Error(`Could not arm benchmark (${armed.status}).`);

    const audioSamples = [];
    const mouthSamples = [];
    const started = performance.now();
    const audioSampler = setInterval(() => {
      const timeMs = performance.now() - started;
      audioSamples.push({ timeMs, value: currentRms() });
    }, 20);
    let sampleVideo = true;
    const captureMouthFrame = () => {
      if (!sampleVideo) return;
      const timeMs = performance.now() - started;
      graphics.drawImage(video, 0, 0, canvas.width, canvas.height);
      const frame = graphics.getImageData(roi.x, roi.y, roi.width, roi.height).data;
      const control = graphics.getImageData(controlRoi.x, controlRoi.y, controlRoi.width, controlRoi.height).data;
      const delta = (before, after) => {
        let total = 0;
        let channels = 0;
        for (let i = 0; i < after.length; i += 4) {
          total += Math.abs(after[i] - before[i]) + Math.abs(after[i + 1] - before[i + 1]) + Math.abs(after[i + 2] - before[i + 2]);
          channels += 3;
        }
        return total / channels;
      };
      mouthSamples.push({ timeMs, value: Math.max(0, delta(previousMouth, frame) - delta(previousControl, control)) });
      previousMouth = frame.slice();
      previousControl = control.slice();
      video.requestVideoFrameCallback(captureMouthFrame);
    };
    video.requestVideoFrameCallback(captureMouthFrame);

    const source = audioContext.createBufferSource();
    source.buffer = injectedAudio;
    source.connect(microphone);
    // Some headless Chrome builds do not render an AudioContext graph that only
    // terminates in MediaStreamAudioDestinationNode. A muted physical-output
    // branch keeps the synthetic microphone clocked without affecting samples.
    const keepAlive = audioContext.createGain();
    keepAlive.gain.value = 0;
    source.connect(keepAlive);
    keepAlive.connect(audioContext.destination);
    const ended = new Promise(resolve => { source.onended = resolve; });
    source.start();
    await sleep(Math.max(0, injectedAudio.duration * 1000 - trailingSilenceMs));
    const speechEndMs = performance.now() - started;
    await fetch(`${baseUrl}/benchmark/webrtc/${sessionId}/speech-end`, { method: "POST" });
    await ended;

    let server;
    const hasSustainedCrossing = (samples, threshold, count) => {
      let run = 0;
      for (const sample of samples) {
        run = sample.timeMs >= speechEndMs && sample.value >= threshold ? run + 1 : 0;
        if (run >= count) return true;
      }
      return false;
    };
    while (performance.now() < deadline) {
      server = await fetch(`${baseUrl}/benchmark/webrtc/${sessionId}`).then(response => response.json());
      const names = new Set(server.events.map(event => event.name));
      const serverComplete = ["stt_final", "llm_first_token", "audio_started", "audio_complete", "first_mouth_frame"].every(name => names.has(name));
      const observedAudio = hasSustainedCrossing(audioSamples, 0.018, 3);
      const observedMouth = hasSustainedCrossing(mouthSamples, 3, 2);
      if (serverComplete && observedAudio && observedMouth) break;
      await sleep(100);
    }

    // Connection state proves ICE/DTLS only. These counters show whether the
    // browser actually encoded/sent microphone RTP and whether media arrived
    // back at the viewer; retain them even when the benchmark case fails.
    const stats = await pc.getStats();
    const values = [...stats.values()];
    const outboundAudio = values.find(item => item.type === "outbound-rtp" && item.kind === "audio");
    const inboundAudio = values.find(item => item.type === "inbound-rtp" && item.kind === "audio");
    const inboundVideo = values.find(item => item.type === "inbound-rtp" && item.kind === "video");
    const selectedPair = values.find(item => item.type === "candidate-pair" &&
      (item.selected || item.nominated) && item.state === "succeeded");
    const codec = outboundAudio?.codecId ? stats.get(outboundAudio.codecId) : undefined;
    const mediaStats = {
      outboundAudioPacketsSent: outboundAudio?.packetsSent ?? 0,
      outboundAudioBytesSent: outboundAudio?.bytesSent ?? 0,
      outboundAudioCodec: codec?.mimeType ?? "unknown",
      outboundAudioPayloadType: codec?.payloadType ?? null,
      inboundAudioPacketsReceived: inboundAudio?.packetsReceived ?? 0,
      inboundAudioBytesReceived: inboundAudio?.bytesReceived ?? 0,
      inboundVideoFramesDecoded: inboundVideo?.framesDecoded ?? 0,
      candidatePairPacketsSent: selectedPair?.packetsSent ?? 0,
      candidatePairPacketsReceived: selectedPair?.packetsReceived ?? 0,
    };

    clearInterval(audioSampler);
    sampleVideo = false;
    recorder.stop();
    await new Promise(resolve => { recorder.onstop = resolve; });
    const recording = new Blob(chunks, { type: recorder.mimeType });
    const recordingBase64 = await new Promise((resolve, reject) => {
      const reader = new FileReader();
      reader.onerror = () => reject(reader.error);
      reader.onload = () => resolve(reader.result.split(",", 2)[1]);
      reader.readAsDataURL(recording);
    });
    await fetch(`${baseUrl}/benchmark/webrtc/${sessionId}`, { method: "DELETE" });
    pc.close();
    await audioContext.close();

    return { sessionId, speechEndMs, audioSamples, mouthSamples, server, mediaStats, recordingBase64 };
  }, { baseUrl, audioBase64, timeoutMs, trailingSilenceMs });
} finally {
  await browser.close();
}

const benchmarkCase = buildCase(observation);
const allMetrics = { ...benchmarkCase.metricsMilliseconds, ...benchmarkCase.metrics };
const summaries = Object.fromEntries(Object.entries(allMetrics).map(([name, value]) => [name, summarize([value])]));
const result = {
  schemaVersion: 1,
  runId: observation.sessionId,
  startedAtUtc: new Date().toISOString(),
  source: { ...(commitSha ? { commitSha } : {}) },
  environment: {
    machineName: os.hostname(),
    operatingSystem: `${os.type()} ${os.release()}`,
    processArchitecture: process.arch,
    framework: `Node ${process.version}; Playwright ${browserChannel}`,
    processorCount: os.cpus().length,
  },
  configuration: {
    suite: "webrtc-lipsync-v1",
    target: baseUrl,
    browserChannel,
    inputAudio: path.basename(audioPath),
    trailingSilenceMilliseconds: String(trailingSilenceMs),
    audioOnsetThresholdRms: "0.018",
    mouthOnsetThresholdRelativeInterFrameRgbDelta: "3",
  },
  cases: [benchmarkCase],
  summaries,
};

await mkdir(path.dirname(outputPath), { recursive: true });
await writeFile(outputPath, `${JSON.stringify(result, null, 2)}\n`);

if (!benchmarkCase.succeeded) {
  await mkdir(path.dirname(recordingPath), { recursive: true });
  await writeFile(recordingPath, Buffer.from(observation.recordingBase64, "base64"));
  console.error(`Benchmark failed; diagnostic recording: ${recordingPath}`);
  process.exitCode = 1;
}

console.log(`Benchmark result: ${outputPath}`);
console.log(JSON.stringify(benchmarkCase.metricsMilliseconds, null, 2));
console.log(JSON.stringify(benchmarkCase.metrics, null, 2));
