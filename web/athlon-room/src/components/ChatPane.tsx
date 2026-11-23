import { useEffect, useRef, useState } from "react";
import { Pencil, Square, Users } from "lucide-react";
import type {
  AgentTraceStep,
  PendingFile,
  RoomMessage,
  RoomMeta,
  RoomPresenceMember,
  RoomTab,
} from "../types";
import { isAgentTraceRunning } from "./AgentTracePanel";
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
  members?: RoomPresenceMember[];
  agentTraces?: Record<string, AgentTraceStep[]>;
  stoppingAgent?: boolean;
  onDraftChange: (v: string) => void;
  onSend: () => void;
  onPickFiles: (files: FileList | null) => void;
  onRemovePending: (id: string) => void;
  onPreviewImage: (src: string, alt: string) => void;
  onOpenSettings?: () => void;
  onStopAgent?: () => void;
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
  members = [],
  agentTraces,
  stoppingAgent,
  onDraftChange,
  onSend,
  onPickFiles,
  onRemovePending,
  onPreviewImage,
  onOpenSettings,
  onStopAgent,
}: ChatPaneProps) {
  const listRef = useRef<HTMLDivElement>(null);
  const membersRef = useRef<HTMLDivElement>(null);
  const [membersOpen, setMembersOpen] = useState(false);
  const lastText = messages[messages.length - 1]?.text ?? "";
  const title = meta?.title ?? tab.title;
  const workspaceLabel =
    meta?.workspaceName ?? tab.workspaceName ?? meta?.workspaceRoot ?? tab.workspaceRoot;
  const traceCount = agentTraces ? Object.values(agentTraces).reduce((n, s) => n + s.length, 0) : 0;
  const memberCount = members.length;
  const agentRunning =
    tab.role === "host" &&
    !!agentTraces &&
    Object.values(agentTraces).some((steps) => isAgentTraceRunning(steps));

  useEffect(() => {
    const el = listRef.current;
    if (!el) return;
    el.scrollTop = el.scrollHeight;
  }, [messages.length, lastText, tab.id, traceCount]);

  useEffect(() => {
    if (!membersOpen) return;
    const onPointerDown = (e: PointerEvent) => {
      if (membersRef.current && !membersRef.current.contains(e.target as Node)) {
        setMembersOpen(false);
      }
    };
    document.addEventListener("pointerdown", onPointerDown);
    return () => document.removeEventListener("pointerdown", onPointerDown);
  }, [membersOpen]);

  useEffect(() => {
    setMembersOpen(false);
  }, [tab.id]);

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
              {` · ${memberCount} 人在线`}
            </p>
          </div>
        </div>
        <div className="flex shrink-0 items-center gap-2">
          <div className="relative" ref={membersRef}>
            <button
              type="button"
              className="inline-flex items-center gap-2 rounded-xl border border-slate-200 px-3 py-2 text-sm text-slate-600 transition hover:border-slate-300 hover:bg-slate-50 hover:text-slate-900 dark:border-zinc-700 dark:text-zinc-300 dark:hover:border-zinc-600 dark:hover:bg-zinc-800 dark:hover:text-white"
              onClick={() => setMembersOpen((v) => !v)}
              title="在线成员"
            >
              <Users size={16} />
              {memberCount}
            </button>
            {membersOpen ? (
              <div className="absolute top-full right-0 z-20 mt-2 w-64 overflow-hidden rounded-2xl border border-slate-200 bg-white shadow-xl dark:border-zinc-700 dark:bg-[#1f1f22]">
                <div className="border-b border-slate-100 px-3 py-2 text-xs font-medium text-slate-500 dark:border-zinc-800 dark:text-zinc-400">
                  已加入（{memberCount}）
                </div>
                <ul className="max-h-64 overflow-y-auto py-1">
                  {members.length === 0 ? (
                    <li className="px-3 py-3 text-sm text-slate-400">暂无在线用户</li>
                  ) : (
                    members.map((m) => {
                      const isSelf = m.senderId === senderId;
                      return (
                        <li
                          key={m.senderId}
                          className="flex items-center gap-2.5 px-3 py-2 text-sm text-slate-700 dark:text-zinc-200"
                        >
                          <span
                            className="flex h-8 w-8 shrink-0 items-center justify-center rounded-xl text-xs font-semibold text-white"
                            style={{ background: avatarColor(m.senderId) }}
                          >
                            {(m.displayName || "?").trim().slice(0, 1).toUpperCase()}
                          </span>
                          <span className="min-w-0 flex-1 truncate">
                            {m.displayName || "用户"}
                            {isSelf ? (
                              <span className="ml-1 text-xs text-slate-400">（我）</span>
                            ) : null}
                          </span>
                          <span className="h-2 w-2 shrink-0 rounded-full bg-emerald-500" title="在线" />
                        </li>
                      );
                    })
                  )}
                </ul>
              </div>
            ) : null}
          </div>
          {agentRunning && onStopAgent ? (
            <button
              type="button"
              className="inline-flex items-center gap-2 rounded-xl border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700 transition hover:border-rose-300 hover:bg-rose-100 disabled:opacity-50 dark:border-rose-500/40 dark:bg-rose-950/40 dark:text-rose-300 dark:hover:bg-rose-950/70"
              onClick={onStopAgent}
              disabled={stoppingAgent}
              title="停止 Agent"
            >
              <Square size={14} className="fill-current" />
              {stoppingAgent ? "停止中" : "停止 Agent"}
            </button>
          ) : null}
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
        </div>
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
              agentTrace={agentTraces?.[m.id]}
              onStopAgent={
                tab.role === "host" && onStopAgent && isAgentTraceRunning(agentTraces?.[m.id])
                  ? onStopAgent
                  : undefined
              }
              stoppingAgent={stoppingAgent}
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

function avatarColor(seed: string): string {
  let hash = 0;
  for (let i = 0; i < seed.length; i++) {
    hash = seed.charCodeAt(i) + ((hash << 5) - hash);
  }
  const colors = ["#0ea5e9", "#0284c7", "#6366f1", "#14b8a6", "#f59e0b", "#8b5cf6", "#64748b", "#f97316"];
  return colors[Math.abs(hash) % colors.length];
}
