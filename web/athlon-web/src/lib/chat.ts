import type { AppSettings, ChatRequestMessage } from "../types";

interface StreamHandlers {
  onChunk: (chunk: string) => void;
  onReasoningChunk?: (chunk: string) => void;
}

export function isAbortError(error: unknown) {
  return error instanceof Error && error.name === "AbortError";
}

/** Page opened from Athlon Agent's loopback static server. */
export function isAgentHostedPage() {
  return typeof window !== "undefined" && window.location.port === "6888";
}

function resolveChatTarget(settings: AppSettings) {
  const apiKey = settings.apiKey.trim();
  const headers: Record<string, string> = {
    "Content-Type": "application/json",
  };
  if (apiKey) {
    headers.Authorization = `Bearer ${apiKey}`;
    return { url: settings.apiUrl, headers };
  }

  if (isAgentHostedPage()) {
    return { url: `${window.location.origin}/v1/chat/completions`, headers };
  }

  throw new Error("请先在设置中填写 API Key");
}

function processDataLine(line: string, handlers: StreamHandlers) {
  if (!line.startsWith("data:")) {
    return;
  }

  const payload = line.slice(5).trim();
  if (!payload || payload === "[DONE]") {
    return;
  }

  try {
    const parsed = JSON.parse(payload) as {
      choices?: Array<{ delta?: { content?: string; reasoning?: string } }>;
    };
    const delta = parsed.choices?.[0]?.delta;
    const reasoning = delta?.reasoning ?? "";
    if (reasoning && handlers.onReasoningChunk) {
      handlers.onReasoningChunk(reasoning);
    }
    const content = delta?.content ?? "";
    if (content) {
      handlers.onChunk(content);
    }
  } catch (error) {
    console.error("解析流式响应失败:", error);
  }
}

export async function streamChatCompletion(
  settings: AppSettings,
  messages: ChatRequestMessage[],
  handlers: StreamHandlers,
  signal: AbortSignal | undefined,
  enableThinking: boolean,
) {
  const { url, headers } = resolveChatTarget(settings);

  const response = await fetch(url, {
    method: "POST",
    headers,
    signal,
    body: JSON.stringify({
      model: settings.model,
      messages,
      temperature: settings.temperature,
      max_tokens: settings.maxTokens,
      stream: true,
      chat_template_kwargs: {
        enable_thinking: enableThinking,
      },
    }),
  });

  if (!response.ok || !response.body) {
    throw new Error(`HTTP error! status: ${response.status}`);
  }

  const reader = response.body.getReader();
  const decoder = new TextDecoder("utf-8");
  let buffer = "";

  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) {
        break;
      }

      buffer += decoder.decode(value, { stream: true });
      const lines = buffer.split("\n");
      buffer = lines.pop() ?? "";

      for (const rawLine of lines) {
        processDataLine(rawLine.trim(), handlers);
      }
    }

    buffer += decoder.decode();
    if (buffer.trim()) {
      processDataLine(buffer.trim(), handlers);
    }
  } finally {
    reader.releaseLock();
  }
}
