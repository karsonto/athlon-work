import { Download, FileIcon } from "lucide-react";
import type { RoomMessage } from "../types";
import { attachmentUrl, formatBytes } from "../lib/api";

interface MessageBubbleProps {
  message: RoomMessage;
  isMine: boolean;
  roomId: string;
  token: string;
  onPreviewImage: (url: string, alt: string) => void;
}

export function MessageBubble({
  message,
  isMine,
  roomId,
  token,
  onPreviewImage,
}: MessageBubbleProps) {
  const align = isMine ? "items-end" : "items-start";
  const bubble = isMine
    ? "bg-[var(--bubble-me)] rounded-2xl rounded-br-md"
    : "bg-[var(--bubble-other)] rounded-2xl rounded-bl-md shadow-sm";

  return (
    <div className={`flex w-full flex-col ${align} gap-1 px-3`}>
      {!isMine ? (
        <span className="px-1 text-xs text-slate-500">{message.displayName}</span>
      ) : null}
      <div className={`max-w-[78%] px-3 py-2 text-[15px] leading-snug text-slate-900 ${bubble}`}>
        {message.text ? <p className="whitespace-pre-wrap break-words">{message.text}</p> : null}
        {message.attachments?.map((att) => {
          const url = attachmentUrl(roomId, token, att.id);
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
              className={`mt-1 flex min-w-[200px] items-center gap-3 rounded-xl bg-black/5 px-3 py-2.5 hover:bg-black/10 ${
                message.text ? "mt-2" : ""
              }`}
            >
              <div className="flex h-10 w-10 items-center justify-center rounded-lg bg-slate-700 text-white">
                <FileIcon size={18} />
              </div>
              <div className="min-w-0 flex-1">
                <div className="truncate text-sm font-medium">{att.fileName}</div>
                <div className="text-xs text-slate-500">{formatBytes(att.size)}</div>
              </div>
              <Download size={16} className="shrink-0 text-slate-600" />
            </a>
          );
        })}
      </div>
      <time className="px-1 text-[10px] text-slate-400">
        {formatTime(message.createdAt)}
      </time>
    </div>
  );
}

function formatTime(iso: string): string {
  try {
    const d = new Date(iso);
    return d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
  } catch {
    return "";
  }
}
