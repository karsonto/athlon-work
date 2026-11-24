import { DEFAULT_SETTINGS } from "./config";
import type {
  AppSettings,
  ChatMessage,
  ConversationHistoryItem,
  DocumentParseMode,
  FileAttachment,
} from "../types";

const HISTORY_KEY = "conversationHistoryList";
const SETTINGS_KEY = "appSettings";
const DOCUMENT_PARSE_MODE_KEY = "documentParseMode";

export function loadHistory(): ConversationHistoryItem[] {
  try {
    const raw = window.localStorage.getItem(HISTORY_KEY);
    if (!raw) {
      return [];
    }

    const parsed = JSON.parse(raw) as ConversationHistoryItem[];
    if (!Array.isArray(parsed)) {
      return [];
    }

    return parsed
      .map((item) => ({
        ...item,
        id: String(item.id),
        timestamp: typeof item.timestamp === "string" ? item.timestamp : new Date().toISOString(),
        messages: Array.isArray(item.messages)
          ? item.messages.map((message, index) => ({
              id: message.id ?? `${String(item.id)}-${index}`,
              role: message.role,
              content: message.content ?? "",
              reasoning:
                typeof (message as Partial<ChatMessage>).reasoning === "string"
                  ? (message as ChatMessage).reasoning
                  : undefined,
              responseTimeMs:
                typeof (message as Partial<ChatMessage>).responseTimeMs === "number"
                  ? (message as ChatMessage).responseTimeMs
                  : undefined,
              attachments: normalizeAttachments(message.attachments),
              documentParseMode: normalizeDocumentParseMode(message.documentParseMode),
            }))
          : [],
      }))
      .filter((item) => item.messages.length > 0);
  } catch (error) {
    console.error("加载历史记录失败:", error);
    return [];
  }
}

function normalizeAttachments(value: unknown): FileAttachment[] | undefined {
  if (!Array.isArray(value) || value.length === 0) {
    return undefined;
  }

  const attachments = value
    .map((item, index) => {
      if (!item || typeof item !== "object") {
        return null;
      }

      const candidate = item as Partial<FileAttachment>;
      const kind = candidate.kind === "image" ? "image" : "text";
      const role = normalizeAttachmentRole(candidate.role);
      return {
        id: candidate.id ?? `attachment-${index}`,
        name: candidate.name ?? `附件-${index + 1}`,
        size: typeof candidate.size === "number" ? candidate.size : 0,
        type: candidate.type ?? (kind === "image" ? "image/*" : "text/plain"),
        kind,
        content: typeof candidate.content === "string" ? candidate.content : "",
        sourceFileName: typeof candidate.sourceFileName === "string" ? candidate.sourceFileName : undefined,
        sourcePage: typeof candidate.sourcePage === "number" ? candidate.sourcePage : undefined,
        role,
        summary: typeof candidate.summary === "string" ? candidate.summary : undefined,
      } satisfies FileAttachment;
    })
    .filter((item): item is FileAttachment => Boolean(item));

  return attachments.length > 0 ? attachments : undefined;
}

function normalizeAttachmentRole(value: unknown): FileAttachment["role"] {
  return value === "document_text" ||
    value === "page_preview" ||
    value === "embedded_image" ||
    value === "raw_upload"
    ? value
    : undefined;
}

function normalizeDocumentParseMode(value: unknown): DocumentParseMode | undefined {
  return value === "text_only" || value === "smart_visual" || value === "all_visual"
    ? value
    : undefined;
}

export function saveHistory(history: ConversationHistoryItem[]) {
  try {
    window.localStorage.setItem(HISTORY_KEY, JSON.stringify(history));
    return true;
  } catch (error) {
    console.error("保存历史记录失败:", error);
    return false;
  }
}

export function clearHistoryStorage() {
  window.localStorage.removeItem(HISTORY_KEY);
}

export function loadSettings(): AppSettings {
  try {
    const raw = window.localStorage.getItem(SETTINGS_KEY);
    if (!raw) {
      return DEFAULT_SETTINGS;
    }

    const parsed = JSON.parse(raw) as Partial<AppSettings>;
    const normalizedTemperature = Number(parsed.temperature);
    const normalizedMaxTokens = Number(parsed.maxTokens);

    return {
      apiUrl: parsed.apiUrl || DEFAULT_SETTINGS.apiUrl,
      apiKey: typeof parsed.apiKey === "string" ? parsed.apiKey : DEFAULT_SETTINGS.apiKey,
      model: parsed.model || DEFAULT_SETTINGS.model,
      temperature: Number.isFinite(normalizedTemperature)
        ? normalizedTemperature
        : DEFAULT_SETTINGS.temperature,
      maxTokens: Number.isFinite(normalizedMaxTokens)
        ? normalizedMaxTokens
        : DEFAULT_SETTINGS.maxTokens,
    };
  } catch (error) {
    console.error("加载设置失败:", error);
    return DEFAULT_SETTINGS;
  }
}

export function saveSettings(settings: AppSettings) {
  try {
    window.localStorage.setItem(SETTINGS_KEY, JSON.stringify(settings));
    return true;
  } catch (error) {
    console.error("保存设置失败:", error);
    return false;
  }
}

export function clearSettingsStorage() {
  window.localStorage.removeItem(SETTINGS_KEY);
}

export function loadDocumentParseMode(): DocumentParseMode {
  try {
    const raw = window.localStorage.getItem(DOCUMENT_PARSE_MODE_KEY);
    return normalizeDocumentParseMode(raw) ?? "smart_visual";
  } catch (error) {
    console.error("加载文档解析模式失败:", error);
    return "smart_visual";
  }
}

export function saveDocumentParseMode(mode: DocumentParseMode) {
  try {
    window.localStorage.setItem(DOCUMENT_PARSE_MODE_KEY, mode);
    return true;
  } catch (error) {
    console.error("保存文档解析模式失败:", error);
    return false;
  }
}
