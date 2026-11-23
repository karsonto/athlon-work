import { useEffect, useRef } from "react";
import { Pencil } from "lucide-react";
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
  onOpenSettings?: () => void;
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
  onOpenSettings,
}: ChatPaneProps) {
  const listRef = useRef<HTMLDivElement>(null);
  const lastText = messages[messages.length - 1]?.text ?? "";
  const title = meta?.title ?? tab.title;
  const workspaceLabel =
    meta?.workspaceName ?? tab.workspaceName ?? meta?.workspaceRoot ?? tab.workspaceRoot;

  useEffect(() => {
    const el = listRef.current;
    if (!el) return;
    el.scrollTop = el.scrollHeight;
  }, [messages.length, lastText, tab.id]);

  return (
    <div className="flex h-full min-w-0 flex-1 flex-col bg-[#f1f5f9] dark:bg-[#0f0f10]">
      <header className="flex h-16 items-center justify-between border-b border-sky-100 bg-white px-6 shadow-sm dark:border-zinc-800 dark:bg-[#18181b] dark:shadow-none">
        <div className="flex min-w-0 items-center gap-3">
          <div className="flex h-10 w-10 shrink-0 items-center justify-center rounded-2xl bg-sky-600 text-sm font-bold text-white shadow-sm">
            A
          </div>
          <div className="min-w-0">
            <h1 className="truncate text-lg font-semibold text-slate-900 dark:text-zinc-100">{title}</h1>
            <p className="truncate text-sm text-slate-500 dark:text-zinc-400">
              {nickname} · {tab.role === "host" ? "本机房间" : "已加入"}
              {workspaceLabel ? ` · ${workspaceLabel}` : ""}
            </p>
          </div>
        </div>
        {tab.role === "host" && onOpenSettings ? (
          <button
            type="button"
            className="inline-flex items-center gap-2 rounded-xl border border-slate-200 px-3 py-2 text-sm text-slate-600 transition hover:border-slate-300 hover:bg-slate-50 hover:text-slate-900 dark:border-zinc-700 dark:text-zinc-300 dark:hover:border-zinc-600 dark:hover:bg-zinc-800 dark:hover:text-white"
            onClick={onOpenSettings}
          >
            <Pencil size={16} />
            设置
          </button>
        ) : null}
      </header>

      <div ref={listRef} className="flex-1 space-y-4 overflow-y-auto py-4">
        {messages.length === 0 ? (
          <p className="py-10 text-center text-sm text-slate-500 dark:text-zinc-400">
            还没有消息，打个招呼吧
          </p>
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
        <div className="bg-rose-50 px-4 py-2 text-center text-sm text-rose-600 dark:bg-rose-950/40 dark:text-rose-300">
          {error}
        </div>
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
