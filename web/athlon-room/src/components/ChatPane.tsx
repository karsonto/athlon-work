import { useEffect, useRef } from "react";
import type { PendingFile, RoomMessage, RoomMeta, RoomTab } from "../types";
import { ComposerBar } from "./ComposerBar";
import { MessageBubble } from "./MessageBubble";

interface ChatPaneProps {
  tab: RoomTab;
  meta: RoomMeta | null;
  nickname: string;
  messages: RoomMessage[];
  draft: string;
  pending: PendingFile[];
  sending: boolean;
  error: string | null;
  senderId: string;
  onDraftChange: (v: string) => void;
  onSend: () => void;
  onPickFiles: (files: FileList | null) => void;
  onRemovePending: (id: string) => void;
  onPreviewImage: (src: string, alt: string) => void;
}

/** Only the current browser's senderId is "me" (right). Agent + all peers stay left. */
function isOwnMessage(message: RoomMessage, localSenderId: string): boolean {
  if (!localSenderId) return false;
  if (message.kind === "agent" || message.senderId === "athlon-agent") {
    return false;
  }
  return message.senderId === localSenderId;
}

export function ChatPane({
  tab,
  meta,
  nickname,
  messages,
  draft,
  pending,
  sending,
  error,
  senderId,
  onDraftChange,
  onSend,
  onPickFiles,
  onRemovePending,
  onPreviewImage,
}: ChatPaneProps) {
  const listRef = useRef<HTMLDivElement>(null);
  const lastText = messages[messages.length - 1]?.text ?? "";

  useEffect(() => {
    const el = listRef.current;
    if (!el) return;
    el.scrollTop = el.scrollHeight;
  }, [messages.length, lastText, tab.id]);

  return (
    <div className="flex h-full min-w-0 flex-1 flex-col bg-[var(--room-bg)]">
      <header className="flex items-center justify-between border-b border-white/10 bg-[var(--room-chrome)] px-4 py-3 text-[var(--text-primary)]">
        <div className="min-w-0">
          <div className="truncate text-sm font-medium">{meta?.title ?? tab.title}</div>
          <div className="text-[11px] text-[var(--text-muted)]">
            以 {nickname} 的身份 · {tab.role === "host" ? "本机房间" : "已加入"}
          </div>
        </div>
      </header>

      <div ref={listRef} className="flex-1 space-y-4 overflow-y-auto py-4">
        {messages.length === 0 ? (
          <p className="py-10 text-center text-sm text-[var(--text-muted)]">还没有消息，打个招呼吧</p>
        ) : (
          messages.map((m) => (
            <MessageBubble
              key={m.id}
              message={m}
              isMine={isOwnMessage(m, senderId)}
              baseUrl={tab.baseUrl}
              roomId={tab.id}
              token={tab.token}
              onPreviewImage={onPreviewImage}
            />
          ))
        )}
      </div>

      {error ? (
        <div className="bg-red-950/60 px-4 py-2 text-center text-sm text-red-300">{error}</div>
      ) : null}

      <ComposerBar
        draft={draft}
        pending={pending}
        sending={sending}
        onDraftChange={onDraftChange}
        onSend={onSend}
        onPickFiles={onPickFiles}
        onRemovePending={onRemovePending}
      />
    </div>
  );
}
