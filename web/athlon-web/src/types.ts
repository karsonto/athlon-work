export type ViewMode = "home" | "chat" | "vllm";

export type Role = "user" | "assistant" | "system";

export type AttachmentKind = "text" | "image";
export type AttachmentRole = "document_text" | "page_preview" | "embedded_image" | "raw_upload";
export type DocumentParseMode = "text_only" | "smart_visual" | "all_visual";

export interface FileAttachment {
  id: string;
  name: string;
  size: number;
  type: string;
  kind: AttachmentKind;
  content: string;
  sourceFileName?: string;
  sourcePage?: number;
  role?: AttachmentRole;
  summary?: string;
}

export interface ChatMessage {
  id: string;
  role: Role;
  content: string;
  /** 模型推理/思维链（流式 delta.reasoning），通常仅 assistant */
  reasoning?: string;
  /** 单次助手回复从请求到结束的总耗时（毫秒），通常仅 assistant */
  responseTimeMs?: number;
  attachments?: FileAttachment[];
  documentParseMode?: DocumentParseMode;
}

export interface ConversationHistoryItem {
  id: string;
  title: string;
  messages: ChatMessage[];
  timestamp: string;
}

export interface AppSettings {
  apiUrl: string;
  apiKey: string;
  model: string;
  temperature: number;
  maxTokens: number;
}

export interface ImageUrlPart {
  type: "image_url";
  image_url: {
    url: string;
  };
}

export interface TextPart {
  type: "text";
  text: string;
}

export type MessagePart = TextPart | ImageUrlPart;

export interface ChatRequestMessage {
  role: Role;
  content: string | MessagePart[];
}
