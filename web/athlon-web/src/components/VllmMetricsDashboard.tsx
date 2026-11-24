import { useCallback, useEffect, useMemo, useState } from "react";
import { parsePrometheusText, type PromSample } from "../lib/prometheus";
import {
  buildVllmSummary,
  summaryToHistoryPoint,
  type MetricsHistoryPoint,
} from "../lib/vllmSummary";
import { deriveMetricsUrlFromApiUrl } from "../lib/config";
import { useRingBufferSnapshots } from "../hooks/useRingBufferSnapshots";
import { useChartIsDark } from "../hooks/useChartIsDark";
import { MetricsCharts } from "./MetricsCharts";

const LS_POLL = "athlonVllmPollSec";
const LS_PROXY = "athlonVllmUseDevProxy";
const HISTORY_MAX = 90;

function formatBytes(n: number) {
  if (!Number.isFinite(n)) {
    return "—";
  }
  if (n < 1024) {
    return `${Math.round(n)} B`;
  }
  const units = ["KiB", "MiB", "GiB", "TiB"];
  let v = n / 1024;
  let i = 0;
  while (v >= 1024 && i < units.length - 1) {
    v /= 1024;
    i += 1;
  }
  return `${v.toFixed(i === 0 ? 1 : 2)} ${units[i]}`;
}

function formatSeconds(s?: number) {
  if (s === undefined || Number.isNaN(s)) {
    return "—";
  }
  if (s >= 60) {
    return `${(s / 60).toFixed(2)} min`;
  }
  return `${s.toFixed(3)} s`;
}

function resolveFetchUrl(metricsUrl: string, useDevProxy: boolean): string | null {
  if (import.meta.env.DEV && useDevProxy) {
    return `${window.location.origin}/__vllm_metrics`;
  }
  const trimmed = metricsUrl.trim();
  if (!trimmed) {
    return null;
  }
  return trimmed;
}

function loadNumber(key: string, fallback: number, min?: number): number {
  try {
    const raw = window.localStorage.getItem(key);
    if (raw === null) {
      return fallback;
    }
    const n = Number(raw);
    if (!Number.isFinite(n)) {
      return fallback;
    }
    if (min !== undefined && n < min) {
      return min;
    }
    return n;
  } catch {
    return fallback;
  }
}

export function VllmMetricsDashboard({ apiUrl, apiKey }: { apiUrl: string; apiKey: string }) {
  const metricsUrl = useMemo(() => deriveMetricsUrlFromApiUrl(apiUrl), [apiUrl]);
  const isDark = useChartIsDark();

  const [pollSec] = useState(() => loadNumber(LS_POLL, 15, 5));
  const [useDevProxy] = useState(() => {
    try {
      return window.localStorage.getItem(LS_PROXY) === "1";
    } catch {
      return false;
    }
  });

  const [samples, setSamples] = useState<PromSample[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [lastOkAt, setLastOkAt] = useState<Date | null>(null);
  const [metricsHistory, pushHistory] = useRingBufferSnapshots<MetricsHistoryPoint>(HISTORY_MAX);

  const fetchMetrics = useCallback(async () => {
    const url = resolveFetchUrl(metricsUrl, useDevProxy);
    if (!url) {
      setError(
        "无法解析指标地址：请在「设置」中填写有效的 API 地址（例如 http://host:8088/v1/chat/completions），将自动请求同 host:port 的 /metrics。",
      );
      setSamples([]);
      return;
    }
    const key = apiKey.trim();
    if (!key) {
      setError("请先在设置中填写 API Key。");
      setSamples([]);
      return;
    }
    setError(null);
    try {
      const res = await fetch(url, {
        cache: "no-store",
        headers: {
          Authorization: `Bearer ${key}`,
        },
      });
      if (!res.ok) {
        throw new Error(`HTTP ${res.status}`);
      }
      const text = await res.text();
      const parsed = parsePrometheusText(text);
      setSamples(parsed);
      setLastOkAt(new Date());
      const summ = buildVllmSummary(parsed, "");
      pushHistory(summaryToHistoryPoint(summ, Date.now()));
    } catch (e) {
      const msg =
        e instanceof Error ? e.message : typeof e === "string" ? e : "请求失败";
      setError(
        `${msg}。若浏览器报 CORS，开发环境可在 report/html/.env.local 中设置 VITE_VLLM_METRICS_PROXY_TARGET（服务基址，不含 /metrics）；可将 localStorage 键 athlonVllmUseDevProxy 设为 1 以走 Vite 同源代理 /__vllm_metrics。生产环境需网关同源或放行 Origin。`,
      );
      setSamples([]);
    }
  }, [metricsUrl, useDevProxy, apiKey, pushHistory]);

  useEffect(() => {
    void fetchMetrics();
    const ms = Math.max(5000, pollSec * 1000);
    const id = window.setInterval(() => {
      void fetchMetrics();
    }, ms);
    return () => window.clearInterval(id);
  }, [fetchMetrics, pollSec]);

  const summary = useMemo(() => buildVllmSummary(samples, ""), [samples]);
  const prefixHitRate =
    summary.prefixQueries !== undefined &&
    summary.prefixQueries > 0 &&
    summary.prefixHits !== undefined
      ? (summary.prefixHits / summary.prefixQueries) * 100
      : undefined;

  return (
    <div className="min-h-0 flex-1 overflow-y-auto bg-base-200 p-4 md:p-6">
      <div className="mx-auto flex max-w-6xl flex-col gap-4">
        <div className="flex flex-col gap-2 md:flex-row md:items-center md:justify-between">
          <h1 className="text-base font-semibold text-base-content">监控指标</h1>
          {lastOkAt ? (
            <span className="text-xs text-base-content/60">
              上次成功：{lastOkAt.toLocaleTimeString()}
            </span>
          ) : null}
        </div>

        {error ? (
          <div className="alert alert-warning text-sm">
            <span>{error}</span>
          </div>
        ) : null}

        <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
          <div className="stats shadow bg-base-100">
            <div className="stat py-3">
              <div className="stat-title text-xs">运行中请求</div>
              <div className="stat-value text-lg">{summary.running ?? "—"}</div>
            </div>
          </div>
          <div className="stats shadow bg-base-100">
            <div className="stat py-3">
              <div className="stat-title text-xs">排队请求</div>
              <div className="stat-value text-lg">{summary.waiting ?? "—"}</div>
            </div>
          </div>
          <div className="stats shadow bg-base-100">
            <div className="stat py-3">
              <div className="stat-title text-xs">KV 缓存占用 %</div>
              <div className="stat-value text-lg">
                {summary.kvCachePerc !== undefined ? summary.kvCachePerc.toFixed(2) : "—"}
              </div>
            </div>
          </div>
          <div className="stats shadow bg-base-100">
            <div className="stat py-3">
              <div className="stat-title text-xs">进程内存</div>
              <div className="stat-value text-base leading-tight">
                {summary.memoryBytes !== undefined ? formatBytes(summary.memoryBytes) : "—"}
              </div>
            </div>
          </div>
        </div>

        <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
          <div className="stats shadow bg-base-100">
            <div className="stat py-3">
              <div className="stat-title text-xs">GPU 缓存占用 %</div>
              <div className="stat-value text-lg">
                {summary.gpuCachePerc !== undefined ? summary.gpuCachePerc.toFixed(2) : "—"}
              </div>
            </div>
          </div>
          <div className="stats shadow bg-base-100">
            <div className="stat py-3">
              <div className="stat-title text-xs">最大并发请求</div>
              <div className="stat-value text-lg">{summary.maxConcurrentRequests ?? "—"}</div>
            </div>
          </div>
          <div className="stats shadow bg-base-100">
            <div className="stat py-3">
              <div className="stat-title text-xs">抢占次数（累计）</div>
              <div className="stat-value text-lg">{summary.numPreemptions ?? "—"}</div>
            </div>
          </div>
          <div className="stats shadow bg-base-100">
            <div className="stat py-3">
              <div className="stat-title text-xs">交换中请求</div>
              <div className="stat-value text-lg">{summary.numRequestsSwapped ?? "—"}</div>
            </div>
          </div>
        </div>

        <MetricsCharts
          history={metricsHistory}
          promptBySource={summary.promptBySource}
          prefixHitRate={prefixHitRate}
          isDark={isDark}
        />

        <div className="card bg-base-100 shadow-sm">
          <div className="card-body gap-2 p-4">
            <h2 className="card-title text-sm">Token / 请求</h2>
            <div className="grid gap-2 text-sm sm:grid-cols-2 lg:grid-cols-3">
              <div>
                生成 tokens（累计）：
                {summary.generationTokens !== undefined ? summary.generationTokens : "—"}
              </div>
              <div>Prompt tokens（累计）：{summary.promptTokens !== undefined ? summary.promptTokens : "—"}</div>
              <div>
                迭代 tokens（累计）：{summary.iterationTokens !== undefined ? summary.iterationTokens : "—"}
              </div>
              <div>请求成功（累计）：{summary.requestSuccess !== undefined ? summary.requestSuccess : "—"}</div>
              <div>请求失败（累计）：{summary.requestFailure !== undefined ? summary.requestFailure : "—"}</div>
              <div>
                HTTP（chat 相关合计）：{summary.httpChatRequests !== undefined ? summary.httpChatRequests : "—"}
              </div>
            </div>
          </div>
        </div>

        <div className="card bg-base-100 shadow-sm">
          <div className="card-body gap-2 p-4">
            <h2 className="card-title text-sm">吞吐（gauge，若导出）</h2>
            <div className="grid gap-2 text-sm sm:grid-cols-2">
              <div>
                平均 prompt 吞吐（tok/s）：
                {summary.avgPromptThroughputTps !== undefined
                  ? summary.avgPromptThroughputTps.toFixed(2)
                  : "—"}
              </div>
              <div>
                平均生成吞吐（tok/s）：
                {summary.avgGenerationThroughputTps !== undefined
                  ? summary.avgGenerationThroughputTps.toFixed(2)
                  : "—"}
              </div>
            </div>
          </div>
        </div>

        <div className="card bg-base-100 shadow-sm">
          <div className="card-body gap-2 p-4">
            <h2 className="card-title text-sm">延迟（均值）</h2>
            <div className="grid gap-2 text-sm sm:grid-cols-2 lg:grid-cols-4">
              <div>E2E：{formatSeconds(summary.e2eLatencyMean)}</div>
              <div>首 token：{formatSeconds(summary.ttftMean)}</div>
              <div>推理：{formatSeconds(summary.inferenceMean)}</div>
              <div>解码：{formatSeconds(summary.decodeLatencyMean)}</div>
            </div>
          </div>
        </div>

        <div className="card bg-base-100 shadow-sm">
          <div className="card-body gap-2 p-4">
            <h2 className="card-title text-sm">Prefix 缓存</h2>
            <div className="grid gap-2 text-sm sm:grid-cols-2">
              <div>查询（累计）：{summary.prefixQueries !== undefined ? summary.prefixQueries : "—"}</div>
              <div>命中（累计）：{summary.prefixHits !== undefined ? summary.prefixHits : "—"}</div>
              <div>
                命中率：{prefixHitRate !== undefined ? `${prefixHitRate.toFixed(2)} %` : "—"}
              </div>
            </div>
            {summary.promptBySource.length > 0 ? (
              <div className="overflow-x-auto">
                <table className="table table-zebra table-xs">
                  <thead>
                    <tr>
                      <th>来源</th>
                      <th>prompt_tokens_by_source_total</th>
                    </tr>
                  </thead>
                  <tbody>
                    {summary.promptBySource.map((row) => (
                      <tr key={row.source}>
                        <td>{row.source}</td>
                        <td>{row.value}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            ) : null}
          </div>
        </div>

        {summary.rawExtras.length > 0 ? (
          <div className="collapse-arrow collapse bg-base-100 shadow-sm">
            <input type="checkbox" />
            <div className="collapse-title text-sm font-medium">其他 gauge（调试，Top {summary.rawExtras.length}）</div>
            <div className="collapse-content text-xs">
              <p className="mb-2 text-base-content/60">
                无标签或≤2 个标签的 gauge 汇总；已排除主面板常用指标与 histogram 后缀序列。
              </p>
              <ul className="grid gap-1 sm:grid-cols-2">
                {summary.rawExtras.map((row) => (
                  <li key={row.name} className="flex justify-between gap-2 border-b border-base-300/50 py-1">
                    <span className="font-mono">{row.name}</span>
                    <span>{Number.isFinite(row.value) ? row.value.toString() : "—"}</span>
                  </li>
                ))}
              </ul>
            </div>
          </div>
        ) : null}

        {summary.cacheConfigRows.length > 0 ? (
          <div className="collapse-arrow collapse bg-base-100 shadow-sm">
            <input type="checkbox" />
            <div className="collapse-title text-sm font-medium">cache_config_info</div>
            <div className="collapse-content text-xs">
              <ul className="list-disc space-y-1 pl-4">
                {summary.cacheConfigRows.map((row, idx) => (
                  <li key={idx}>
                    {Object.entries(row)
                      .filter(([k]) => k !== "__name__")
                      .map(([k, v]) => `${k}=${v}`)
                      .join(", ")}
                  </li>
                ))}
              </ul>
            </div>
          </div>
        ) : null}
      </div>
    </div>
  );
}
