import { useEffect, useRef, useState } from "react";
import { ImagePlus, Paperclip, Send, X } from "lucide-react";
import type { PendingFile } from "../types";
import { formatBytes } from "../lib/api";

interface ComposerBarProps {
  draft: string;
  pending: PendingFile[];
  sending: boolean;
  onDraftChange: (v: string) => void;
  onSend: () => void;
  onPickFiles: (files: FileList | null) => void;
  onRemovePending: (id: string) => void;
}

/** Match incomplete @agent mention at caret (word-boundary @ + optional agent prefix). */
function findAgentMention(
  text: string,
  caret: number,
): { start: number; end: number; query: string } | null {
  const before = text.slice(0, caret);
  const match = before.match(/(^|[\s])(@[Aa]?[Gg]?[Ee]?[Nn]?[Tt]?)$/);
  if (!match) return null;
  const token = match[2] ?? "";
  if (token.toLowerCase() === "@agent") return null;
  const start = before.length - token.length;
  return { start, end: caret, query: token.slice(1).toLowerCase() };
}

export function ComposerBar({
  draft,
  pending,
  sending,
  onDraftChange,
  onSend,
  onPickFiles,
  onRemovePending,
}: ComposerBarProps) {
  const textareaRef = useRef<HTMLTextAreaElement>(null);
  const [caret, setCaret] = useState(0);
  const [mentionOpen, setMentionOpen] = useState(false);
  const pendingCaretRef = useRef<number | null>(null);

  const mention = findAgentMention(draft, caret);
  const showMention = mentionOpen && mention !== null;

  useEffect(() => {
    setMentionOpen(mention !== null);
  }, [draft, caret, mention !== null]);

  useEffect(() => {
    if (pendingCaretRef.current == null) return;
    const el = textareaRef.current;
    const pos = pendingCaretRef.current;
    pendingCaretRef.current = null;
    if (!el) return;
    requestAnimationFrame(() => {
      el.focus();
      el.setSelectionRange(pos, pos);
      setCaret(pos);
    });
  }, [draft]);

  const applyAgentMention = () => {
    if (!mention) return;
    const next = `${draft.slice(0, mention.start)}@agent ${draft.slice(mention.end)}`;
    const nextCaret = mention.start + "@agent ".length;
    pendingCaretRef.current = nextCaret;
    setMentionOpen(false);
    onDraftChange(next);
  };

  const syncCaret = () => {
    const el = textareaRef.current;
    if (el) setCaret(el.selectionStart);
  };

  return (
    <div
      className="border-t border-sky-100 bg-white px-4 pt-3 pb-[max(0.85rem,env(safe-area-inset-bottom))] dark:border-zinc-800 dark:bg-[#18181b]"
      onDragOver={(e) => e.preventDefault()}
      onDrop={(e) => {
        e.preventDefault();
        onPickFiles(e.dataTransfer.files);
      }}
    >
      {pending.length > 0 ? (
        <div className="mb-3 flex gap-2 overflow-x-auto pb-1">
          {pending.map((p) => (
            <div
              key={p.id}
              className="relative flex shrink-0 items-center gap-2 rounded-2xl border border-slate-200 bg-slate-50 px-2 py-1.5 dark:border-zinc-700 dark:bg-zinc-800"
            >
              {p.previewUrl ? (
                <img src={p.previewUrl} alt="" className="h-12 w-12 rounded-xl object-cover" />
              ) : (
                <div className="flex h-12 w-12 items-center justify-center rounded-xl bg-slate-200 text-xs text-slate-500 dark:bg-zinc-700 dark:text-zinc-300">
                  文件
                </div>
              )}
              <div className="max-w-[120px]">
                <div className="truncate text-xs font-medium text-slate-800 dark:text-zinc-100">{p.file.name}</div>
                <div className="text-[10px] text-slate-500 dark:text-zinc-400">{formatBytes(p.file.size)}</div>
              </div>
              <button
                type="button"
                className="absolute -top-1.5 -right-1.5 rounded-full bg-slate-500 p-0.5 text-white"
                onClick={() => onRemovePending(p.id)}
                aria-label="移除"
              >
                <X size={12} />
              </button>
            </div>
          ))}
        </div>
      ) : null}

      <div className="relative mx-auto max-w-4xl">
        {showMention ? (
          <div className="absolute right-0 bottom-full left-0 z-10 mb-2 overflow-hidden rounded-2xl border border-slate-200 bg-white shadow-lg dark:border-zinc-700 dark:bg-[#27272a]">
            <button
              type="button"
              className="flex w-full items-center gap-3 px-4 py-3 text-left transition hover:bg-sky-50 dark:hover:bg-sky-500/10"
              onMouseDown={(e) => {
                e.preventDefault();
                applyAgentMention();
              }}
            >
              <span className="flex h-9 w-9 shrink-0 items-center justify-center rounded-2xl bg-sky-600 text-sm font-bold text-white">
                A
              </span>
              <span className="min-w-0">
                <span className="block text-sm font-medium text-slate-900 dark:text-zinc-100">@agent</span>
                <span className="block text-xs text-slate-500 dark:text-zinc-400">Athlon 助手</span>
              </span>
            </button>
          </div>
        ) : null}

        <div className="flex items-end gap-2 rounded-[1.5rem] border border-slate-200 bg-slate-50/90 p-2 shadow-sm ring-1 ring-white dark:border-zinc-700 dark:bg-[#27272a] dark:ring-zinc-800">
          <label className="mb-0.5 cursor-pointer rounded-xl p-2 text-slate-500 transition hover:bg-white hover:text-sky-700 dark:text-zinc-400 dark:hover:bg-zinc-800 dark:hover:text-sky-300">
            <ImagePlus size={20} />
            <input
              type="file"
              accept="image/*"
              multiple
              className="hidden"
              onChange={(e) => {
                onPickFiles(e.target.files);
                e.currentTarget.value = "";
              }}
            />
          </label>
          <label className="mb-0.5 cursor-pointer rounded-xl p-2 text-slate-500 transition hover:bg-white hover:text-sky-700 dark:text-zinc-400 dark:hover:bg-zinc-800 dark:hover:text-sky-300">
            <Paperclip size={20} />
            <input
              type="file"
              multiple
              className="hidden"
              onChange={(e) => {
                onPickFiles(e.target.files);
                e.currentTarget.value = "";
              }}
            />
          </label>
          <textarea
            ref={textareaRef}
            className="max-h-28 min-h-[42px] flex-1 resize-none bg-transparent px-2 py-2.5 text-[15px] text-slate-900 outline-none placeholder:text-slate-400 dark:text-zinc-100 dark:placeholder:text-zinc-500"
            rows={1}
            placeholder="输入消息，@agent 可召唤助手"
            value={draft}
            disabled={sending}
            onChange={(e) => {
              onDraftChange(e.target.value);
              setCaret(e.target.selectionStart);
            }}
            onClick={syncCaret}
            onKeyUp={syncCaret}
            onSelect={syncCaret}
            onKeyDown={(e) => {
              if (showMention && (e.key === "Enter" || e.key === "Tab" || e.key === " ")) {
                e.preventDefault();
                applyAgentMention();
                return;
              }
              if (showMention && e.key === "Escape") {
                e.preventDefault();
                setMentionOpen(false);
                return;
              }
              if (e.key === "Enter" && !e.shiftKey) {
                e.preventDefault();
                onSend();
              }
            }}
          />
          <button
            type="button"
            className="mb-0.5 flex h-10 w-10 shrink-0 items-center justify-center rounded-2xl bg-sky-600 text-white shadow-sm transition hover:bg-sky-700 disabled:opacity-40"
            disabled={sending || (!draft.trim() && pending.length === 0)}
            onClick={onSend}
            aria-label="发送"
          >
            <Send size={18} />
          </button>
        </div>
      </div>
    </div>
  );
}
