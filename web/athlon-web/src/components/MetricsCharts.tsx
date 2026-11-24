import {
  Bar,
  BarChart,
  CartesianGrid,
  Cell,
  ComposedChart,
  Legend,
  Line,
  Pie,
  PieChart,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from "recharts";
import type { MetricsHistoryPoint } from "../lib/vllmSummary";

function chartPalette(isDark: boolean) {
  if (isDark) {
    return {
      axis: "#a1a1aa",
      grid: "#3f3f46",
      running: "#38bdf8",
      waiting: "#fb923c",
      kvCache: "#4ade80",
      memory: "#c084fc",
      bar: "#38bdf8",
      hitFill: "#4ade80",
      missFill: "#3f3f46",
      tooltipBg: "oklch(0.21 0.006 285.885)",
      tooltipBorder: "#3f3f46",
    };
  }
  return {
    axis: "#52525b",
    grid: "#e4e4e7",
    running: "#0284c7",
    waiting: "#ea580c",
    kvCache: "#16a34a",
    memory: "#9333ea",
    bar: "#0284c7",
    hitFill: "#16a34a",
    missFill: "#d4d4d8",
    tooltipBg: "oklch(1 0 0)",
    tooltipBorder: "#d4d4d8",
  };
}

export function MetricsCharts({
  history,
  promptBySource,
  prefixHitRate,
  isDark,
}: {
  history: MetricsHistoryPoint[];
  promptBySource: { source: string; value: number }[];
  prefixHitRate?: number;
  isDark: boolean;
}) {
  const p = chartPalette(isDark);
  const hasHistory = history.length > 0;
  const hasMemorySeries = history.some(
    (h) => h.memoryGiB !== undefined && Number.isFinite(h.memoryGiB),
  );

  const barData = promptBySource.map((r) => ({
    source: r.source.length > 24 ? `${r.source.slice(0, 22)}…` : r.source,
    tokens: r.value,
  }));

  const pieData =
    prefixHitRate !== undefined && Number.isFinite(prefixHitRate)
      ? [
          { name: "命中", value: Math.min(100, Math.max(0, prefixHitRate)) },
          { name: "未命中", value: Math.max(0, 100 - prefixHitRate) },
        ]
      : [];

  return (
    <div className="grid gap-4 lg:grid-cols-2">
      <div className="card bg-base-100 shadow-sm">
        <div className="card-body gap-2 p-4">
          <h2 className="card-title text-sm">短期趋势（浏览器内存）</h2>
          {hasHistory ? (
            <div className="h-56 w-full min-w-0">
              <ResponsiveContainer width="100%" height="100%">
                <ComposedChart data={history} margin={{ top: 8, right: hasMemorySeries ? 24 : 8, left: 0, bottom: 0 }}>
                  <CartesianGrid strokeDasharray="3 3" stroke={p.grid} />
                  <XAxis dataKey="timeLabel" tick={{ fontSize: 10, fill: p.axis }} />
                  <YAxis yAxisId="left" tick={{ fontSize: 10, fill: p.axis }} width={36} allowDecimals />
                  {hasMemorySeries ? (
                    <YAxis
                      yAxisId="right"
                      orientation="right"
                      tick={{ fontSize: 10, fill: p.memory }}
                      width={44}
                      label={{
                        value: "GiB",
                        angle: -90,
                        position: "insideRight",
                        fill: p.memory,
                        fontSize: 10,
                      }}
                    />
                  ) : null}
                  <Tooltip
                    contentStyle={{
                      background: p.tooltipBg,
                      border: `1px solid ${p.tooltipBorder}`,
                      fontSize: 12,
                      borderRadius: 8,
                    }}
                  />
                  <Legend wrapperStyle={{ fontSize: 12 }} />
                  <Line
                    yAxisId="left"
                    type="monotone"
                    dataKey="running"
                    name="运行中"
                    stroke={p.running}
                    dot={false}
                    strokeWidth={2}
                    connectNulls
                  />
                  <Line
                    yAxisId="left"
                    type="monotone"
                    dataKey="waitingBy"
                    name="排队"
                    stroke={p.waiting}
                    dot={false}
                    strokeWidth={2}
                    connectNulls
                  />
                  <Line
                    yAxisId="left"
                    type="monotone"
                    dataKey="kvCachePerc"
                    name="KV %"
                    stroke={p.kvCache}
                    dot={false}
                    strokeWidth={2}
                    connectNulls
                  />
                  {hasMemorySeries ? (
                    <Line
                      yAxisId="right"
                      type="monotone"
                      dataKey="memoryGiB"
                      name="内存 GiB"
                      stroke={p.memory}
                      dot={false}
                      strokeWidth={2}
                      connectNulls
                    />
                  ) : null}
                </ComposedChart>
              </ResponsiveContainer>
            </div>
          ) : (
            <div className="h-56" aria-hidden />
          )}
        </div>
      </div>

      <div className="card bg-base-100 shadow-sm">
        <div className="card-body gap-2 p-4">
          <h2 className="card-title text-sm">Prompt tokens 按来源</h2>
          {barData.length > 0 ? (
            <div className="h-56 w-full min-w-0">
              <ResponsiveContainer width="100%" height="100%">
                <BarChart data={barData} layout="vertical" margin={{ top: 8, right: 16, left: 8, bottom: 8 }}>
                  <CartesianGrid strokeDasharray="3 3" stroke={p.grid} horizontal={false} />
                  <XAxis type="number" tick={{ fontSize: 10, fill: p.axis }} />
                  <YAxis type="category" dataKey="source" width={100} tick={{ fontSize: 10, fill: p.axis }} />
                  <Tooltip
                    contentStyle={{
                      background: p.tooltipBg,
                      border: `1px solid ${p.tooltipBorder}`,
                      fontSize: 12,
                      borderRadius: 8,
                    }}
                  />
                  <Bar dataKey="tokens" name="tokens" fill={p.bar} radius={[0, 4, 4, 0]} />
                </BarChart>
              </ResponsiveContainer>
            </div>
          ) : (
            <div className="h-56" aria-hidden />
          )}
        </div>
      </div>

      <div className="card bg-base-100 shadow-sm lg:col-span-2">
        <div className="card-body flex flex-col gap-4 p-4 sm:flex-row sm:items-center">
          <div className="min-w-0 flex-1">
            <h2 className="card-title text-sm">Prefix 命中率（当前快照）</h2>
          </div>
          {pieData.length > 0 ? (
            <div className="mx-auto h-44 w-44 shrink-0 sm:mx-0">
              <ResponsiveContainer width="100%" height="100%">
                <PieChart>
                  <Pie
                    data={pieData}
                    dataKey="value"
                    nameKey="name"
                    cx="50%"
                    cy="50%"
                    innerRadius="58%"
                    outerRadius="88%"
                    paddingAngle={1}
                  >
                    <Cell fill={p.hitFill} />
                    <Cell fill={p.missFill} />
                  </Pie>
                  <Tooltip
                    formatter={(v: number) => `${v.toFixed(1)}%`}
                    contentStyle={{
                      background: p.tooltipBg,
                      border: `1px solid ${p.tooltipBorder}`,
                      fontSize: 12,
                      borderRadius: 8,
                    }}
                  />
                  <Legend wrapperStyle={{ fontSize: 12 }} />
                </PieChart>
              </ResponsiveContainer>
            </div>
          ) : (
            <div className="h-44 w-44 shrink-0 sm:mx-0" aria-hidden />
          )}
        </div>
      </div>
    </div>
  );
}
