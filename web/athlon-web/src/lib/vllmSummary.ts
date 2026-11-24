import {
  findSamples,
  firstValue,
  histogramMean,
  sumMetricValues,
  type PromSample,
} from "./prometheus";

function metricNames(base: string): string[] {
  if (base.startsWith("vllm:")) {
    return [base];
  }
  return [base, `vllm:${base}`];
}

function pickMetricName(samples: PromSample[], base: string): string | undefined {
  for (const name of metricNames(base)) {
    if (samples.some((s) => s.name === name)) {
      return name;
    }
  }
  return undefined;
}

function firstValueForBase(
  samples: PromSample[],
  base: string,
  filt?: Record<string, string>,
): number | undefined {
  const n = pickMetricName(samples, base);
  if (!n) {
    return undefined;
  }
  const withFilt = firstValue(samples, n, filt);
  if (withFilt !== undefined) {
    return withFilt;
  }
  if (filt && Object.keys(filt).length > 0) {
    return firstValue(samples, n);
  }
  return undefined;
}

function sumForBase(samples: PromSample[], base: string, filt?: Record<string, string>): number {
  const n = pickMetricName(samples, base);
  if (!n) {
    return 0;
  }
  const withFilt = sumMetricValues(samples, n, filt);
  if (findSamples(samples, n, filt).length > 0) {
    return withFilt;
  }
  if (filt && Object.keys(filt).length > 0) {
    return sumMetricValues(samples, n);
  }
  return withFilt;
}

function sumForBaseOrUndefined(
  samples: PromSample[],
  base: string,
  filt?: Record<string, string>,
): number | undefined {
  const n = pickMetricName(samples, base);
  if (!n) {
    return undefined;
  }
  return sumForBase(samples, base, filt);
}

function histogramMeanForBase(
  samples: PromSample[],
  base: string,
  filt?: Record<string, string>,
): number | undefined {
  const n = pickMetricName(samples, base);
  if (!n) {
    return undefined;
  }
  const m = histogramMean(samples, n, filt);
  if (m !== undefined) {
    return m;
  }
  if (filt && Object.keys(filt).length > 0) {
    return histogramMean(samples, n);
  }
  return undefined;
}

/** 已在主面板展示的指标名（含 vllm: 前缀形式由 basename 表示） */
const PRIMARY_METRIC_BASES = new Set([
  "num_requests_running",
  "num_requests_waiting",
  "kv_cache_usage_perc",
  "process_resident_memory_bytes",
  "generation_tokens_total",
  "prompt_tokens_total",
  "request_success_total",
  "http_requests_total",
  "e2e_request_latency_seconds",
  "time_to_first_token_seconds",
  "request_inference_time_seconds",
  "prefix_cache_queries_total",
  "prefix_cache_hits_total",
  "prompt_tokens_by_source_total",
  "cache_config_info",
  "gpu_cache_usage_perc",
  "num_preemptions_total",
  "request_failure_total",
  "max_concurrent_requests",
  "num_requests_swapped",
  "iteration_tokens_total",
  "request_decode_time_seconds",
  "avg_prompt_throughput_toks_per_s",
  "avg_generation_throughput_toks_per_s",
]);

function basenameForRaw(name: string): string {
  return name.startsWith("vllm:") ? name.slice(5) : name;
}

function shouldSkipRawMetricName(name: string): boolean {
  const base = basenameForRaw(name);
  if (PRIMARY_METRIC_BASES.has(base)) {
    return true;
  }
  if (
    base.endsWith("_bucket") ||
    base.endsWith("_sum") ||
    base.endsWith("_count") ||
    base.endsWith("_created") ||
    base.endsWith("_total")
  ) {
    return true;
  }
  if (base.endsWith("_info") || base.includes("config_info")) {
    return true;
  }
  return false;
}

export function buildRawGaugeExtras(samples: PromSample[], limit = 20): { name: string; value: number }[] {
  const acc = new Map<string, number>();
  for (const s of samples) {
    if (shouldSkipRawMetricName(s.name)) {
      continue;
    }
    const labelCount = Object.keys(s.labels).filter((k) => k && k !== "__name__").length;
    if (labelCount > 2) {
      continue;
    }
    const base = basenameForRaw(s.name);
    acc.set(base, (acc.get(base) ?? 0) + s.value);
  }
  const rows = [...acc.entries()].map(([name, value]) => ({ name, value }));
  rows.sort((a, b) => Math.abs(b.value) - Math.abs(a.value));
  return rows.slice(0, limit);
}

export type VllmSummary = {
  running?: number;
  waiting?: number;
  kvCachePerc?: number;
  gpuCachePerc?: number;
  memoryBytes?: number;
  numRequestsSwapped?: number;
  maxConcurrentRequests?: number;
  generationTokens?: number;
  promptTokens?: number;
  iterationTokens?: number;
  requestSuccess?: number;
  requestFailure?: number;
  httpChatRequests?: number;
  numPreemptions?: number;
  e2eLatencyMean?: number;
  ttftMean?: number;
  inferenceMean?: number;
  decodeLatencyMean?: number;
  avgPromptThroughputTps?: number;
  avgGenerationThroughputTps?: number;
  prefixQueries?: number;
  prefixHits?: number;
  promptBySource: { source: string; value: number }[];
  cacheConfigRows: Record<string, string>[];
  rawExtras: { name: string; value: number }[];
};

export function buildVllmSummary(samples: PromSample[], modelName: string): VllmSummary {
  const filt = modelName ? { model_name: modelName } : undefined;

  const httpChatRequests = (() => {
    const n = pickMetricName(samples, "http_requests_total");
    if (!n) {
      return undefined;
    }
    const all = findSamples(samples, n, filt);
    const list = all.length ? all : findSamples(samples, n);
    const chatish = list.filter(
      (s) =>
        (s.labels.handler ?? "").includes("chat/completions") ||
        (s.labels.path ?? "").includes("chat/completions") ||
        (s.labels.route ?? "").includes("chat/completions") ||
        (s.labels.endpoint ?? "").toLowerCase().includes("chat"),
    );
    const use = chatish.length ? chatish : list;
    return use.reduce((a, s) => a + s.value, 0);
  })();

  const promptBySource: { source: string; value: number }[] = [];
  const ptBase = pickMetricName(samples, "prompt_tokens_by_source_total");
  if (ptBase) {
    const rows = findSamples(samples, ptBase, filt);
    const effective = rows.length ? rows : findSamples(samples, ptBase);
    const map = new Map<string, number>();
    for (const s of effective) {
      const src = s.labels.source ?? s.labels.origin ?? "(default)";
      map.set(src, (map.get(src) ?? 0) + s.value);
    }
    for (const [source, value] of map.entries()) {
      promptBySource.push({ source, value });
    }
    promptBySource.sort((a, b) => b.value - a.value);
  }

  const cacheConfigRows: Record<string, string>[] = [];
  const ccBase = pickMetricName(samples, "cache_config_info");
  if (ccBase) {
    for (const s of findSamples(samples, ccBase)) {
      cacheConfigRows.push({ ...s.labels });
    }
  }

  const requestFailureSum = sumForBaseOrUndefined(samples, "request_failure_total", filt);
  const numPreemptionsSum = sumForBaseOrUndefined(samples, "num_preemptions_total", filt);
  const iterationTok = sumForBaseOrUndefined(samples, "iteration_tokens_total", filt);

  const summary: VllmSummary = {
    running: firstValueForBase(samples, "num_requests_running", filt),
    waiting: firstValueForBase(samples, "num_requests_waiting", filt),
    kvCachePerc: firstValueForBase(samples, "kv_cache_usage_perc", filt),
    gpuCachePerc: firstValueForBase(samples, "gpu_cache_usage_perc", filt),
    memoryBytes: firstValueForBase(samples, "process_resident_memory_bytes", filt),
    numRequestsSwapped: firstValueForBase(samples, "num_requests_swapped", filt),
    maxConcurrentRequests: firstValueForBase(samples, "max_concurrent_requests", filt),
    generationTokens: sumForBaseOrUndefined(samples, "generation_tokens_total", filt),
    promptTokens: sumForBaseOrUndefined(samples, "prompt_tokens_total", filt),
    iterationTokens: iterationTok,
    requestSuccess: sumForBaseOrUndefined(samples, "request_success_total", filt),
    requestFailure: requestFailureSum,
    httpChatRequests,
    numPreemptions: numPreemptionsSum,
    e2eLatencyMean: histogramMeanForBase(samples, "e2e_request_latency_seconds", filt),
    ttftMean: histogramMeanForBase(samples, "time_to_first_token_seconds", filt),
    inferenceMean: histogramMeanForBase(samples, "request_inference_time_seconds", filt),
    decodeLatencyMean: histogramMeanForBase(samples, "request_decode_time_seconds", filt),
    avgPromptThroughputTps: firstValueForBase(samples, "avg_prompt_throughput_toks_per_s", filt),
    avgGenerationThroughputTps: firstValueForBase(samples, "avg_generation_throughput_toks_per_s", filt),
    prefixQueries: sumForBaseOrUndefined(samples, "prefix_cache_queries_total", filt),
    prefixHits: sumForBaseOrUndefined(samples, "prefix_cache_hits_total", filt),
    promptBySource,
    cacheConfigRows,
    rawExtras: [],
  };

  summary.rawExtras = buildRawGaugeExtras(samples, 20);
  return summary;
}

export type MetricsHistoryPoint = {
  t: number;
  timeLabel: string;
  running?: number;
  waitingBy?: number;
  kvCachePerc?: number;
  memoryGiB?: number;
};

export function summaryToHistoryPoint(summary: VllmSummary, now = Date.now()): MetricsHistoryPoint {
  return {
    t: now,
    timeLabel: new Date(now).toLocaleTimeString(),
    running: summary.running,
    waitingBy: summary.waiting,
    kvCachePerc: summary.kvCachePerc,
    memoryGiB:
      summary.memoryBytes !== undefined && Number.isFinite(summary.memoryBytes)
        ? summary.memoryBytes / 1024 ** 3
        : undefined,
  };
}
