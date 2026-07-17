import { mkdir, readFile, readdir, writeFile } from "node:fs/promises";
import path from "node:path";
import process from "node:process";
import { fileURLToPath } from "node:url";
import { summarize } from "./analysis.mjs";

const milliseconds = result => result.cases.flatMap(item =>
  Object.entries(item.metricsMilliseconds ?? {}).map(([name, value]) => ({ name, value })));
const unitMetrics = result => result.cases.flatMap(item =>
  Object.entries(item.metrics ?? {}).map(([name, value]) => ({ name, value })));

function p50(values) {
  return summarize(values).p50;
}

function valuesFor(result, group, name) {
  return result.cases
    .map(item => item[group]?.[name])
    .filter(value => Number.isFinite(value));
}

function derivedValues(result, endMetric, startMetric) {
  return result.cases
    .map(item => {
      const end = item.metricsMilliseconds?.[endMetric];
      const start = item.metricsMilliseconds?.[startMetric];
      return Number.isFinite(end) && Number.isFinite(start) ? end - start : null;
    })
    .filter(value => value != null);
}

function metricSummary(items) {
  return Object.fromEntries(
    [...new Set(items.map(item => item.name))]
      .sort()
      .map(name => [name, summarize(items.filter(item => item.name === name).map(item => item.value))]),
  );
}

export function combineResults(results, metadata) {
  if (!results.length) throw new Error("At least one benchmark result is required.");
  if (results.some(result => result.schemaVersion !== 1)) {
    throw new Error("Only schema-v1 benchmark results can be combined.");
  }

  const cases = results.flatMap(result => result.cases).map((item, iteration) => ({
    ...item,
    iteration,
  }));
  const combined = {
    schemaVersion: 1,
    runId: metadata.runId,
    startedAtUtc: results.map(result => result.startedAtUtc).sort()[0],
    source: {
      commitSha: metadata.commitSha,
      imageDigest: metadata.imageDigest,
    },
    environment: {
      ...results[0].environment,
      machineName: metadata.runner ?? "benchmark-runner",
      framework: results[0].environment.framework.replace(
        /Playwright \S+$/,
        `Playwright ${results[0].configuration.browserChannel ?? "browser"}`,
      ),
    },
    configuration: {
      ...results[0].configuration,
      target: metadata.target ?? results[0].configuration.target,
      deployment: metadata.deployment,
      runner: metadata.runner ?? "benchmark-runner",
      llmModel: metadata.llmModel,
      samples: String(cases.length),
      // A GitHub-hosted runner cannot observe Kubernetes restart counts. Keep
      // that distinction explicit instead of publishing the string "undefined".
      deploymentRestarts: metadata.deploymentRestarts == null
        ? "not observed"
        : String(metadata.deploymentRestarts),
    },
    cases,
    summaries: {
      ...metricSummary(milliseconds({ cases })),
      ...metricSummary(unitMetrics({ cases })),
    },
  };

  const browserOffsets = valuesFor(combined, "metrics", "received_mouth_minus_audio_ms");
  const spread = browserOffsets.length ? Math.max(...browserOffsets) - Math.min(...browserOffsets) : 0;
  combined.configuration.browserMouthDetectionQuality = spread > 1500
    ? `provisional: ${Math.round(spread)} ms sample spread`
    : "consistent";
  return combined;
}

export function describeResult(result, fileName = "") {
  const metric = name => p50(valuesFor(result, "metricsMilliseconds", name));
  const unit = name => p50(valuesFor(result, "metrics", name));
  const serverOffset = p50(derivedValues(
    result,
    "server_speech_end_to_first_mouth_frame_ms",
    "server_speech_end_to_audio_started_ms",
  ));
  const stages = {
    stt: metric("server_speech_end_to_stt_final_ms"),
    llm: p50(derivedValues(
      result,
      "server_speech_end_to_llm_first_token_ms",
      "server_speech_end_to_stt_final_ms",
    )),
    tts: p50(derivedValues(
      result,
      "server_speech_end_to_audio_started_ms",
      "server_speech_end_to_llm_first_token_ms",
    )),
    lipsync: serverOffset,
  };
  const browserOffsets = valuesFor(result, "metrics", "received_mouth_minus_audio_ms");
  const browserSpread = browserOffsets.length
    ? Math.max(...browserOffsets) - Math.min(...browserOffsets)
    : null;
  return {
    fileName,
    utc: result.startedAtUtc,
    revision: result.source?.commitSha?.slice(0, 8) || "—",
    imageDigest: result.source?.imageDigest || "—",
    target: result.configuration?.target || "—",
    deployment: result.configuration?.deployment || "—",
    llmModel: result.configuration?.llmModel || "—",
    samples: result.cases.length,
    succeeded: result.cases.filter(item => item.succeeded).length,
    deploymentRestarts: result.configuration?.deploymentRestarts ?? "—",
    stt: metric("server_speech_end_to_stt_final_ms"),
    llmFirst: metric("server_speech_end_to_llm_first_token_ms"),
    audio: metric("client_speech_end_to_audio_ms"),
    mouth: metric("client_speech_end_to_first_mouth_ms"),
    serverOffset,
    browserOffset: unit("received_mouth_minus_audio_ms"),
    browserMinimum: browserOffsets.length ? Math.min(...browserOffsets) : null,
    browserMaximum: browserOffsets.length ? Math.max(...browserOffsets) : null,
    browserSpread,
    wav2lip: metric("wav2lip_mean_inference_ms"),
    encode: metric("video_mean_encode_ms"),
    fps: unit("effective_fps"),
    dropped: unit("dropped_ticks"),
    stages,
  };
}

const fmt = value => Number.isFinite(value) ? Math.round(value).toLocaleString("en-US") : "—";
const fmtDecimal = value => Number.isFinite(value) ? value.toFixed(1) : "—";
const quality = run => run.browserSpread > 1500 ? "⚠ provisional" : "stable";
const escapeXml = value => String(value)
  .replaceAll("&", "&amp;")
  .replaceAll("<", "&lt;")
  .replaceAll(">", "&gt;");

export function buildHistory(runs) {
  const latest = [...runs].sort((a, b) => b.utc.localeCompare(a.utc))[0];
  const stageLabels = {
    stt: "STT final",
    llm: "LLM first token",
    tts: "TTS to first audio",
    lipsync: "first mouth frame",
  };
  const bottleneck = latest
    ? Object.entries(latest.stages).filter(([, value]) => Number.isFinite(value)).sort((a, b) => b[1] - a[1])[0]
    : null;
  const lines = [
    "# Max WebRTC benchmark history",
    "",
    "Generated from schema-v1 benchmark JSON. Latencies are p50 in milliseconds; lower is better.",
    "",
    "![Latest pipeline decomposition](charts/pipeline.svg)",
    "",
    "![Latency history](charts/latency.svg)",
    "",
  ];

  if (latest && bottleneck) {
    lines.push(
      "## Latest finding",
      "",
      `The dominant measured stage is **${stageLabels[bottleneck[0]]}** at **${fmt(bottleneck[1])} ms p50**. ` +
        `${latest.succeeded}/${latest.samples} samples succeeded and the deployment recorded ` +
        `${latest.deploymentRestarts} restarts during the batch.`,
      "",
    );
  }

  lines.push(
    "## Viewer latency and lip sync",
    "",
    "| run (UTC) | deployment | LLM model | revision | n | STT final | LLM first token | speech→audio | speech→mouth | server A/V offset | browser A/V offset | quality |",
    "|---|---|---|---|---:|---:|---:|---:|---:|---:|---:|---|",
  );
  for (const run of [...runs].sort((a, b) => b.utc.localeCompare(a.utc))) {
    const timestamp = new Date(run.utc).toISOString().replace("T", " ").slice(0, 16);
    const revision = run.fileName ? `[${run.revision}](runs/${run.fileName})` : run.revision;
    lines.push(
      `| ${timestamp} | ${run.deployment} | ${run.llmModel} | ${revision} | ${run.samples} | ${fmt(run.stt)} | ` +
      `${fmt(run.llmFirst)} | ${fmt(run.audio)} | ${fmt(run.mouth)} | ${fmt(run.serverOffset)} | ` +
      `${fmt(run.browserOffset)} | ${quality(run)} |`,
    );
  }

  lines.push(
    "",
    "## Renderer health",
    "",
    "| run (UTC) | revision | Wav2Lip mean/frame | video encode mean/frame | effective FPS | dropped ticks | browser offset range |",
    "|---|---|---:|---:|---:|---:|---:|",
  );
  for (const run of [...runs].sort((a, b) => b.utc.localeCompare(a.utc))) {
    const timestamp = new Date(run.utc).toISOString().replace("T", " ").slice(0, 16);
    lines.push(
      `| ${timestamp} | ${run.revision} | ${fmt(run.wav2lip)} | ${fmt(run.encode)} | ` +
      `${fmtDecimal(run.fps)} | ${fmt(run.dropped)} | ${fmt(run.browserMinimum)}–${fmt(run.browserMaximum)} |`,
    );
  }

  lines.push(
    "",
    "## Metric definitions and quality",
    "",
    "- **STT final**, **LLM first token**, **first audio**, and **first mouth** start at the deterministic input's end-of-speech boundary.",
    "- **Server A/V offset** is first generated Wav2Lip mouth frame minus audio handoff. Positive values mean the mouth trails audio.",
    "- **Browser A/V offset** is received mouth-motion onset minus received audio onset. It is the closest measure of viewer experience, but fixed-region motion detection can miss onset when the avatar moves; a sample spread above 1,500 ms is marked provisional and the full range is shown.",
    "- **Wav2Lip/frame** and **video encode/frame** are renderer costs. Effective FPS and dropped ticks expose whether the deployment keeps up with the nominal 25 FPS schedule.",
    "- Raw schema-v1 JSON is linked from each revision. Measurements from different benchmark implementations should only be compared when their definitions and prompt audio match.",
    "",
  );
  return lines.join("\n");
}

export function buildPipelineSvg(run) {
  const stages = [
    ["STT final", run?.stages.stt, "#4c78a8"],
    ["LLM first token", run?.stages.llm, "#59a14f"],
    ["TTS to first audio", run?.stages.tts, "#e15759"],
    ["First mouth frame", run?.stages.lipsync, "#b279a2"],
  ];
  const maximum = Math.max(1, ...stages.map(([, value]) => value || 0));
  const width = 900;
  const left = 180;
  const plotWidth = 650;
  const rows = stages.map(([name, value, color], index) => {
    const y = 58 + index * 58;
    const barWidth = Number.isFinite(value) ? (value / maximum) * plotWidth : 0;
    return [
      `<text x="${left - 10}" y="${y + 17}" text-anchor="end" font-size="13" fill="#333">${escapeXml(name)}</text>`,
      `<rect x="${left}" y="${y}" width="${barWidth.toFixed(1)}" height="24" rx="3" fill="${color}"/>`,
      `<text x="${Math.min(left + barWidth + 8, width - 65).toFixed(1)}" y="${y + 17}" font-size="12" fill="#333">${fmt(value)} ms</text>`,
    ].join("\n");
  }).join("\n");
  return `<svg xmlns="http://www.w3.org/2000/svg" width="${width}" height="320" viewBox="0 0 ${width} 320" font-family="sans-serif">
<rect width="100%" height="100%" fill="white"/>
<text x="24" y="28" font-size="17" fill="#222">Latest pipeline stage latency — p50, lower is better</text>
${rows}
</svg>
`;
}

export function buildLatencySvg(runs) {
  const ordered = [...runs].sort((a, b) => a.utc.localeCompare(b.utc));
  const series = [
    ["first audio (e2e)", "#e15759", run => run.audio],
    ["LLM first token", "#59a14f", run => run.llmFirst],
    ["server A/V offset", "#b279a2", run => run.serverOffset],
  ];
  const width = 900;
  const height = 360;
  const left = 70;
  const top = 45;
  const plotWidth = 800;
  const plotHeight = 245;
  const maximum = Math.max(1, ...ordered.flatMap(run => series.map(([, , pick]) => pick(run) || 0))) * 1.08;
  const x = index => ordered.length < 2 ? left + plotWidth / 2 : left + plotWidth * index / (ordered.length - 1);
  const y = value => top + plotHeight - plotHeight * value / maximum;
  const grid = Array.from({ length: 6 }, (_, index) => {
    const value = maximum * index / 5;
    const rowY = top + plotHeight - plotHeight * index / 5;
    return `<line x1="${left}" y1="${rowY.toFixed(1)}" x2="${left + plotWidth}" y2="${rowY.toFixed(1)}" stroke="#e5e5e5"/>
<text x="${left - 8}" y="${(rowY + 4).toFixed(1)}" text-anchor="end" font-size="11" fill="#666">${fmt(value)}</text>`;
  }).join("\n");
  const paths = series.map(([name, color, pick], seriesIndex) => {
    const points = ordered
      .map((run, index) => Number.isFinite(pick(run)) ? `${x(index).toFixed(1)},${y(pick(run)).toFixed(1)}` : null)
      .filter(Boolean);
    const dots = ordered.map((run, index) => Number.isFinite(pick(run))
      ? `<circle cx="${x(index).toFixed(1)}" cy="${y(pick(run)).toFixed(1)}" r="4" fill="${color}"/>`
      : "").join("\n");
    const legendX = left + seriesIndex * 220;
    return `<polyline points="${points.join(" ")}" fill="none" stroke="${color}" stroke-width="2"/>
${dots}
<rect x="${legendX}" y="326" width="11" height="11" fill="${color}"/>
<text x="${legendX + 16}" y="336" font-size="11" fill="#333">${escapeXml(name)}</text>`;
  }).join("\n");
  const labels = ordered.map((run, index) =>
    `<text x="${x(index).toFixed(1)}" y="308" text-anchor="middle" font-size="10" fill="#666">${escapeXml(run.revision)}</text>`).join("\n");
  return `<svg xmlns="http://www.w3.org/2000/svg" width="${width}" height="${height}" viewBox="0 0 ${width} ${height}" font-family="sans-serif">
<rect width="100%" height="100%" fill="white"/>
<text x="${left}" y="24" font-size="17" fill="#222">WebRTC latency history — p50 ms, lower is better</text>
${grid}
${paths}
${labels}
</svg>
`;
}

function optionValues(name) {
  return process.argv.flatMap((item, index) =>
    item === `--${name}` && process.argv[index + 1] ? [process.argv[index + 1]] : []);
}

async function main() {
  const inputPaths = optionValues("input");
  const one = name => optionValues(name)[0];
  if (!inputPaths.length) throw new Error("Pass one or more --input <result.json> options.");
  const runOut = one("run-out");
  const historyDir = one("history-dir");
  if (!runOut || !historyDir) throw new Error("Pass --run-out and --history-dir.");

  const input = await Promise.all(inputPaths.map(async item => JSON.parse(await readFile(item, "utf8"))));
  const combined = combineResults(input, {
    runId: one("run-id") ?? `webrtc-${one("commit-sha")}`,
    commitSha: one("commit-sha"),
    imageDigest: one("image-digest"),
    target: one("target"),
    deployment: one("deployment") ?? "—",
    runner: one("runner") ?? "benchmark-runner",
    llmModel: one("llm-model") ?? "—",
    deploymentRestarts: one("deployment-restarts") ?? null,
  });
  await mkdir(path.dirname(runOut), { recursive: true });
  await writeFile(runOut, `${JSON.stringify(combined, null, 2)}\n`);

  const runsDir = path.join(historyDir, "runs");
  const files = (await readdir(runsDir)).filter(name => name.endsWith(".json"));
  const runs = await Promise.all(files.map(async name =>
    describeResult(JSON.parse(await readFile(path.join(runsDir, name), "utf8")), name)));
  const latest = [...runs].sort((a, b) => b.utc.localeCompare(a.utc))[0];
  await mkdir(path.join(historyDir, "charts"), { recursive: true });
  await writeFile(path.join(historyDir, "history.md"), buildHistory(runs));
  await writeFile(path.join(historyDir, "charts", "pipeline.svg"), buildPipelineSvg(latest));
  await writeFile(path.join(historyDir, "charts", "latency.svg"), buildLatencySvg(runs));
}

const isMain = process.argv[1] &&
  path.resolve(process.argv[1]) === path.resolve(fileURLToPath(import.meta.url));
if (isMain) {
  main().catch(error => {
    console.error(error.message);
    process.exitCode = 1;
  });
}
