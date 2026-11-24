import { useState } from "react";
import { Download, Eye, FileIcon } from "lucide-react";
import type { AgentTraceStep, RoomMessage } from "../types";
import { attachmentUrl, formatBytes } from "../lib/api";
import { isHtmlAttachment } from "../lib/htmlPreview";
import { AgentTracePanel } from "./AgentTracePanel";
import { MarkdownContent } from "./MarkdownContent";

interface MessageBubbleProps {
  message: RoomMessage;
  isMine: boolean;
  baseUrl: string;
  roomId: string;
  token: string;
  onPreviewImage: (url: string, alt: string) => void;
  onPreviewHtml?: (html: string, title?: string) => void;
  /** Host-only live @agent tool/model trace for this bubble. */
  agentTrace?: AgentTraceStep[];
  onStopAgent?: () => void;
  stoppingAgent?: boolean;
}

export function MessageBubble({
  message,
  isMine,
  baseUrl,
  roomId,
  token,
  onPreviewImage,
  onPreviewHtml,
  agentTrace,
  onStopAgent,
  stoppingAgent,
}: MessageBubbleProps) {
  const [htmlLoadingId, setHtmlLoadingId] = useState<string | null>(null);
  const isAgent = message.kind === "agent" || message.senderId === "athlon-agent";
  const name = message.displayName || (isAgent ? "Athlon" : "用户");

  const avatar = isAgent ? (
    <div
      className="flex h-10 w-10 shrink-0 items-center justify-center rounded-2xl bg-sky-600 text-sm font-semibold text-white shadow-sm"
      title={name}
    >
      A
    </div>
  ) : (
    <div
      className="flex h-10 w-10 shrink-0 items-center justify-center rounded-2xl text-sm font-semibold text-white shadow-sm"
      style={{ background: avatarColor(message.senderId || name) }}
      title={name}
    >
      {avatarInitial(name)}
    </div>
  );

  // Agent bubbles match report/html: max-w-[85%] of the chat pane (no rem cap).
  const bubbleClass = isMine
    ? "rounded-3xl bg-sky-600 px-4 py-3 text-white shadow-sm"
    : isAgent
      ? "w-full max-w-[85%] rounded-3xl bg-white px-5 py-4 text-slate-800 shadow-sm ring-1 ring-slate-200 dark:bg-[#262628] dark:text-zinc-100 dark:ring-zinc-700"
      : "rounded-3xl bg-white px-4 py-3 text-slate-800 ring-1 ring-slate-200 shadow-sm dark:bg-[#27272a] dark:text-zinc-100 dark:ring-zinc-700";

  const body = (
    <div
      className={`flex min-w-0 flex-col ${isMine ? "items-end" : "items-start"} ${
        isAgent ? "w-full" : "max-w-[min(78%,28rem)]"
      }`}
    >
      <div className={`mb-1 flex w-full items-baseline gap-2 ${isMine ? "justify-end" : "justify-start"}`}>
        <span className="truncate text-xs text-slate-500 dark:text-zinc-400">{name}</span>
        <time className="shrink-0 text-[10px] text-slate-400 dark:text-zinc-500">
          {formatTime(message.createdAt)}
        </time>
      </div>
      <div className={`text-[15px] leading-relaxed ${bubbleClass}`}>
        {message.text ? (
          isAgent ? (
            <MarkdownContent content={message.text} onPreviewHtml={onPreviewHtml} />
          ) : (
            <p className="whitespace-pre-wrap break-words">{message.text}</p>
          )
        ) : null}
        {message.attachments?.map((att) => {
          const url = attachmentUrl(baseUrl, roomId, token, att.id);
          if (att.isImage || att.mimeType.startsWith("image/")) {
            return (
              <button
                key={att.id}
                type="button"
                className={`block overflow-hidden rounded-2xl ${message.text ? "mt-2" : ""}`}
                onClick={() => onPreviewImage(url, att.fileName)}
              >
                <img
                  src={url}
                  alt={att.fileName}
                  className="max-h-56 max-w-full object-cover"
                  loading="lazy"
                />
              </button>
            );
          }

          const canPreviewHtml = !!onPreviewHtml && isHtmlAttachment(att.fileName, att.mimeType);
          const loading = htmlLoadingId === att.id;

          return (
            <div
              key={att.id}
              className={`flex min-w-[200px] items-center gap-3 rounded-2xl px-3 py-2.5 ${
                isMine
                  ? "bg-sky-700/40"
                  : "bg-slate-50 dark:bg-zinc-800"
              } ${message.text ? "mt-2" : ""}`}
            >
              <div
                className={`flex h-10 w-10 items-center justify-center rounded-xl ${
                  isMine ? "bg-white/15 text-white" : "bg-sky-50 text-sky-700 dark:bg-sky-500/15 dark:text-sky-300"
                }`}
              >
                <FileIcon size={18} />
              </div>
              <div className="min-w-0 flex-1">
                <div className="truncate text-sm font-medium">{att.fileName}</div>
                <div className={`text-xs ${isMine ? "opacity-80" : "text-slate-500 dark:text-zinc-400"}`}>
                  {formatBytes(att.size)}
                </div>
              </div>
              {canPreviewHtml ? (
                <button
                  type="button"
                  className={`inline-flex shrink-0 items-center gap-1 rounded-lg px-2 py-1.5 text-xs font-medium transition ${
                    isMine
                      ? "bg-white/15 text-white hover:bg-white/25"
                      : "bg-sky-600 text-white hover:bg-sky-700"
                  } disabled:opacity-50`}
                  disabled={loading}
                  onClick={() => {
                    setHtmlLoadingId(att.id);
                    void fetch(url)
                      .then(async (res) => {
                        if (!res.ok) throw new Error("load failed");
                        const text = await res.text();
                        onPreviewHtml?.(text, att.fileName);
                      })
                      .catch(() => {
                        /* ignore */
                      })
                      .finally(() => setHtmlLoadingId(null));
                  }}
                >
                  <Eye size={14} />
                  {loading ? "…" : "预览"}
                </button>
              ) : null}
              <a
                href={url}
                download={att.fileName}
                target="_blank"
                rel="noreferrer"
                className={`shrink-0 opacity-80 transition hover:opacity-100 ${
                  isMine ? "text-white" : "text-slate-600 dark:text-zinc-300"
                }`}
                title="下载"
              >
                <Download size={16} />
              </a>
            </div>
          );
        })}
        {isAgent && agentTrace && agentTrace.length > 0 ? (
          <AgentTracePanel
            steps={agentTrace}
            onStop={onStopAgent}
            stopping={stoppingAgent}
          />
        ) : null}
      </div>
    </div>
  );

  return (
    <div className={`flex w-full px-4 ${isMine ? "justify-end" : "justify-start"}`}>
      <div
        className={`flex gap-2.5 ${isMine ? "max-w-[min(85%,32rem)] flex-row-reverse" : "w-full flex-row"}`}
      >
        {avatar}
        {body}
      </div>
    </div>
  );
}

function avatarInitial(name: string): string {
  const trimmed = name.trim();
  if (!trimmed) return "?";
  return trimmed.slice(0, 1).toUpperCase();
}

function avatarColor(seed: string): string {
  let hash = 0;
  for (let i = 0; i < seed.length; i++) {
    hash = seed.charCodeAt(i) + ((hash << 5) - hash);
  }
  const colors = [
    "#0ea5e9",
    "#0284c7",
    "#6366f1",
    "#14b8a6",
    "#f59e0b",
    "#8b5cf6",
    "#64748b",
    "#f97316",
  ];
  return colors[Math.abs(hash) % colors.length];
}

function formatTime(iso: string): string {
  try {
    const d = new Date(iso);
    return d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
  } catch {
    return "";
  }
}
