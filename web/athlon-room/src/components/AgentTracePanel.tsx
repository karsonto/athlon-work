import { useEffect, useState } from "react";
import { ChevronDown, ChevronRight, Wrench } from "lucide-react";
import type { AgentTraceStep } from "../types";

interface AgentTracePanelProps {
  steps: AgentTraceStep[];
}

function stepLabel(step: AgentTraceStep): string {
  switch (step.type) {
    case "run_started":
      return "开始";
    case "tool_start":
      return `工具 · ${step.toolName || "unknown"}`;
    case "tool_args":
      return `参数 · ${step.toolName || "unknown"}`;
    case "tool_result":
      return `结果 · ${step.toolName || "unknown"}`;
    case "reasoning":
      return "推理";
    case "run_finished":
      return "完成";
    case "error":
      return "错误";
    default:
      return step.type;
  }
}

function stepTone(type: string): string {
  switch (type) {
    case "error":
      return "border-rose-200 bg-rose-50/80 text-rose-800 dark:border-rose-500/30 dark:bg-rose-950/30 dark:text-rose-200";
    case "tool_result":
      return "border-emerald-200 bg-emerald-50/70 text-emerald-900 dark:border-emerald-500/30 dark:bg-emerald-950/20 dark:text-emerald-200";
    case "tool_start":
    case "tool_args":
      return "border-sky-200 bg-sky-50/70 text-sky-900 dark:border-sky-500/30 dark:bg-sky-950/20 dark:text-sky-200";
    case "run_finished":
      return "border-slate-200 bg-slate-50 text-slate-700 dark:border-zinc-700 dark:bg-zinc-800/60 dark:text-zinc-200";
    default:
      return "border-slate-200 bg-white text-slate-700 dark:border-zinc-700 dark:bg-zinc-900/40 dark:text-zinc-300";
  }
}

export function AgentTracePanel({ steps }: AgentTracePanelProps) {
  const [open, setOpen] = useState(true);
  const running = steps.some((s) => s.type === "run_started")
    && !steps.some((s) => s.type === "run_finished" || s.type === "error");

  useEffect(() => {
    if (running) setOpen(true);
  }, [running, steps.length]);

  if (steps.length === 0) return null;

  return (
    <div className="mt-3 overflow-hidden rounded-2xl border border-violet-200/80 bg-violet-50/40 dark:border-violet-500/25 dark:bg-violet-950/20">
      <button
        type="button"
        className="flex w-full items-center gap-2 px-3 py-2.5 text-left text-xs font-medium text-violet-900 transition hover:bg-violet-100/50 dark:text-violet-100 dark:hover:bg-violet-900/25"
        onClick={() => setOpen((v) => !v)}
      >
        {open ? <ChevronDown size={14} /> : <ChevronRight size={14} />}
        <Wrench size={14} className="shrink-0" />
        <span className="min-w-0 flex-1 truncate">
          运行轨迹
          {running ? " · 进行中" : ""}
          <span className="ml-1 font-normal text-violet-700/70 dark:text-violet-200/70">
            ({steps.length})
          </span>
        </span>
      </button>

      {open ? (
        <div className="max-h-64 space-y-1.5 overflow-y-auto border-t border-violet-200/70 px-3 py-2.5 dark:border-violet-500/20">
          {steps.map((step) => (
            <details
              key={step.id}
              className={`rounded-xl border px-2.5 py-1.5 text-[11px] leading-5 ${stepTone(step.type)}`}
            >
              <summary className="cursor-pointer list-none font-medium [&::-webkit-details-marker]:hidden">
                <span className="inline-flex w-full items-center justify-between gap-2">
                  <span className="min-w-0 truncate">{stepLabel(step)}</span>
                  <time className="shrink-0 opacity-60">
                    {formatClock(step.at)}
                  </time>
                </span>
              </summary>
              {step.summary ? (
                <pre className="mt-1.5 max-h-32 overflow-auto whitespace-pre-wrap break-words font-mono text-[10px] opacity-90">
                  {step.summary}
                </pre>
              ) : null}
            </details>
          ))}
        </div>
      ) : null}
    </div>
  );
}

function formatClock(iso: string): string {
  try {
    return new Date(iso).toLocaleTimeString([], {
      hour: "2-digit",
      minute: "2-digit",
      second: "2-digit",
    });
  } catch {
    return "";
  }
}
