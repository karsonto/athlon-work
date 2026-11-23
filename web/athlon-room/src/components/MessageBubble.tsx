import { Download, FileIcon } from "lucide-react";
import type { RoomMessage } from "../types";
import { attachmentUrl, formatBytes } from "../lib/api";

interface MessageBubbleProps {
  message: RoomMessage;
  isMine: boolean;
  baseUrl: string;
  roomId: string;
  token: string;
  onPreviewImage: (url: string, alt: string) => void;
}

export function MessageBubble({
  message,
  isMine,
  baseUrl,
  roomId,
  token,
  onPreviewImage,
}: MessageBubbleProps) {
  const isAgent = message.kind === "agent" || message.senderId === "athlon-agent";
  const name = message.displayName || (isAgent ? "Athlon" : "用户");
  const avatar = (
    <div
      className="flex h-10 w-10 shrink-0 items-center justify-center rounded-md text-sm font-semibold text-white"
      style={{ background: avatarColor(message.senderId || name) }}
      title={name}
    >
      {avatarInitial(name)}
    </div>
  );

  const bubbleClass = isMine
    ? "bubble-tail-right bg-[var(--bubble-me)] text-white"
    : isAgent
      ? "bubble-tail-left is-agent bg-[var(--bubble-agent)] text-[var(--text-primary)]"
      : "bubble-tail-left bg-[var(--bubble-other)] text-[var(--text-primary)]";

  const body = (
    <div className={`min-w-0 max-w-[min(78%,28rem)] ${isMine ? "items-end" : "items-start"} flex flex-col`}>
      <div className={`mb-1 flex w-full items-baseline gap-2 ${isMine ? "justify-end" : "justify-start"}`}>
        <span className="truncate text-xs text-[var(--text-muted)]">{name}</span>
        <time className="shrink-0 text-[10px] text-[var(--text-muted)]/80">
          {formatTime(message.createdAt)}
        </time>
      </div>
      <div className={`rounded-xl px-3.5 py-2.5 text-[15px] leading-relaxed ${bubbleClass}`}>
        {message.text ? <p className="whitespace-pre-wrap break-words">{message.text}</p> : null}
        {message.attachments?.map((att) => {
          const url = attachmentUrl(baseUrl, roomId, token, att.id);
          if (att.isImage || att.mimeType.startsWith("image/")) {
            return (
              <button
                key={att.id}
                type="button"
                className={`block overflow-hidden rounded-lg ${message.text ? "mt-2" : ""}`}
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

          return (
            <a
              key={att.id}
              href={url}
              download={att.fileName}
              target="_blank"
              rel="noreferrer"
              className={`flex min-w-[200px] items-center gap-3 rounded-lg bg-black/25 px-3 py-2.5 hover:bg-black/35 ${
                message.text ? "mt-2" : ""
              }`}
            >
              <div className="flex h-10 w-10 items-center justify-center rounded-lg bg-white/10 text-white">
                <FileIcon size={18} />
              </div>
              <div className="min-w-0 flex-1">
                <div className="truncate text-sm font-medium">{att.fileName}</div>
                <div className="text-xs opacity-70">{formatBytes(att.size)}</div>
              </div>
              <Download size={16} className="shrink-0 opacity-80" />
            </a>
          );
        })}
      </div>
    </div>
  );

  // Own messages hug the right; everyone else (incl. agent) hugs the left.
  return (
    <div
      className={`flex w-full px-3 ${isMine ? "justify-end" : "justify-start"}`}
    >
      <div
        className={`flex max-w-[min(85%,32rem)] gap-2.5 ${
          isMine ? "flex-row-reverse" : "flex-row"
        }`}
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
    "#5b8ff9",
    "#61ddaa",
    "#65789b",
    "#f6bd16",
    "#7262fd",
    "#78d3f8",
    "#9661bc",
    "#f6903d",
    "#008685",
    "#f08bb4",
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
