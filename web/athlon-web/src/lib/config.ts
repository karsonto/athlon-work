import type { AppSettings } from "../types";

function buildDefaultApiUrl() {
  const envUrl = import.meta.env.VITE_API_URL as string | undefined;
  if (envUrl?.trim()) {
    return envUrl.trim();
  }

  if (typeof window === "undefined") {
    return "http://localhost:8088/v1/chat/completions";
  }

  const { protocol, hostname } = window.location;
  return `${protocol}//${hostname}:8088/v1/chat/completions`;
}

function buildDefaultMaxTokens() {
  const envValue = import.meta.env.VITE_MAX_TOKENS as string | undefined;
  const parsedValue = Number(envValue);
  return Number.isFinite(parsedValue) && parsedValue > 0 ? parsedValue : 3000;
}

export const DEFAULT_SETTINGS: AppSettings = {
  apiUrl: buildDefaultApiUrl(),
  apiKey: (import.meta.env.VITE_API_KEY as string | undefined)?.trim() || "",
  model: (import.meta.env.VITE_DEFAULT_MODEL as string | undefined)?.trim() || "athlon-coder",
  temperature: 0.3,
  maxTokens: buildDefaultMaxTokens(),
};

/** 从聊天 API 完整地址推导 Prometheus 指标地址（与 API 同 host:port，路径固定为 /metrics） */
export function deriveMetricsUrlFromApiUrl(apiUrl: string): string {
  const trimmed = apiUrl.trim();
  if (!trimmed) {
    return "";
  }
  try {
    const base = typeof window !== "undefined" ? window.location.origin : "http://localhost";
    const u = new URL(trimmed, base);
    return `${u.origin}/metrics`;
  } catch {
    return "";
  }
}

export const EXAMPLE_PROMPTS = [
  {
    id: "hello",
    label: "Hello World 示例",
    prompt: "请帮我写一个 Python 的 Hello World 程序",
  },
  {
    id: "sort",
    label: "排序算法",
    prompt: "请解释一下快速排序算法并给出 Java 实现",
  },
  {
    id: "api",
    label: "API 请求示例",
    prompt: "如何在 JavaScript 中发送 GET 请求获取 JSON 数据？",
  },
];

export const ACCEPTED_FILE_TYPES =
  ".txt,.js,.py,.java,.cpp,.c,.html,.css,.json,.xml,.md,.csv,.yml,.yaml,.pdf,.docx,.xlsx,.xls,.ppt,.pptx,.png,.jpg,.jpeg,.webp,.gif";
