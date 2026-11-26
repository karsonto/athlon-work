import { useEffect, useMemo, useRef, useState } from "react";
import { ImagePlus, Paperclip, Send, Smile, X } from "lucide-react";
import type { PendingFile, RoomPresenceMember } from "../types";
import { formatBytes } from "../lib/api";
import {
  agentMentionMatches,
  filterMentionCandidates,
  findMentionAtCaret,
  insertMentionToken,
  mentionableMembers,
} from "../lib/mentions";
import { AthlonIcon } from "./AthlonIcon";

interface ComposerBarProps {
  draft: string;
  pending: PendingFile[];
  sending: boolean;
  members?: RoomPresenceMember[];
  selfSenderId?: string;
  onDraftChange: (v: string) => void;
  onSend: () => void;
  onPickFiles: (files: FileList | File[] | null) => void;
  onRemovePending: (id: string) => void;
}

function clipboardImageFile(file: File): File {
  const hasName = Boolean(file.name && file.name !== "image.png" && !file.name.startsWith("image."));
  if (hasName) return file;
  const ext = (file.type.split("/")[1] || "png").replace(/[^a-z0-9]/gi, "") || "png";
  return new File([file], `clipboard-${Date.now()}.${ext}`, {
    type: file.type || "image/png",
    lastModified: Date.now(),
  });
}

function extractClipboardImages(data: DataTransfer | null): File[] {
  if (!data) return [];
  const files: File[] = [];
  const items = data.items;
  if (items) {
    for (let i = 0; i < items.length; i++) {
      const item = items[i];
      if (item.kind !== "file" || !item.type.startsWith("image/")) continue;
      const raw = item.getAsFile();
      if (raw) files.push(clipboardImageFile(raw));
    }
  }
  if (files.length > 0) return files;
  // Some browsers expose screenshot files on clipboardData.files instead of items.
  for (const file of Array.from(data.files ?? [])) {
    if (file.type.startsWith("image/")) files.push(clipboardImageFile(file));
  }
  return files;
}

const EMOJI_GROUPS: { label: string; emojis: string[] }[] = [
  {
    label: "常用",
    emojis: [
      "😀", "😁", "😂", "🤣", "😊", "😍", "🤩", "😘", "🙂", "😉",
      "🤗", "🤔", "😏", "😌", "😴", "😷", "🤒", "🤯", "😎", "🥳",
      "😢", "😭", "😤", "😡", "🥺", "😱", "🤭", "🙄", "😵", "💀",
    ],
  },
  {
    label: "手势",
    emojis: [
      "👍", "👎", "👏", "🙌", "👋", "🤝", "✌️", "🤞", "🤟", "👌",
      "👊", "✊", "💪", "🙏", "🫶", "💯", "✅", "❌", "⭐", "🔥",
    ],
  },
  {
    label: "物品",
    emojis: [
      "🎉", "🎊", "🎈", "🎁", "💡", "📌", "📎", "📝", "📚", "💻",
      "📱", "☕", "🍺", "🍕", "🍰", "🌈", "☀️", "🌙", "⚡", "❤️",
    ],
  },
];

function avatarColor(seed: string): string {
  let hash = 0;
  for (let i = 0; i < seed.length; i++) {
    hash = seed.charCodeAt(i) + ((hash << 5) - hash);
  }
  const colors = ["#0ea5e9", "#0284c7", "#6366f1", "#14b8a6", "#f59e0b", "#8b5cf6", "#64748b", "#f97316"];
  return colors[Math.abs(hash) % colors.length];
}

export function ComposerBar({
  draft,
  pending,
  sending,
  members = [],
  selfSenderId = "",
  onDraftChange,
  onSend,
  onPickFiles,
  onRemovePending,
}: ComposerBarProps) {
  const textareaRef = useRef<HTMLTextAreaElement>(null);
  const emojiRef = useRef<HTMLDivElement>(null);
  const mentionRef = useRef<HTMLDivElement>(null);
  const [caret, setCaret] = useState(0);
  const [mentionOpen, setMentionOpen] = useState(false);
  const [emojiOpen, setEmojiOpen] = useState(false);
  const [activeIndex, setActiveIndex] = useState(0);
  const pendingCaretRef = useRef<number | null>(null);

  const mention = findMentionAtCaret(draft, caret);
  const people = useMemo(
    () => mentionableMembers(members, selfSenderId),
    [members, selfSenderId],
  );
  const filteredPeople = useMemo(
    () => (mention ? filterMentionCandidates(people, mention.query) : []),
    [people, mention],
  );
  const showAgent = mention ? agentMentionMatches(mention.query) : false;
  const optionsCount = (showAgent ? 1 : 0) + filteredPeople.length;
  const showMention = mentionOpen && mention !== null && optionsCount > 0;

  useEffect(() => {
    setMentionOpen(mention !== null);
    setActiveIndex(0);
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
    });
    setCaret(pos);
  }, [draft]);

  useEffect(() => {
    if (!emojiOpen) return;
    const onPointerDown = (e: PointerEvent) => {
      if (emojiRef.current && !emojiRef.current.contains(e.target as Node)) {
        setEmojiOpen(false);
      }
    };
    document.addEventListener("pointerdown", onPointerDown);
    return () => document.removeEventListener("pointerdown", onPointerDown);
  }, [emojiOpen]);

  useEffect(() => {
    if (!showMention) return;
    const onPointerDown = (e: PointerEvent) => {
      if (mentionRef.current && !mentionRef.current.contains(e.target as Node)) {
        setMentionOpen(false);
      }
    };
    document.addEventListener("pointerdown", onPointerDown);
    return () => document.removeEventListener("pointerdown", onPointerDown);
  }, [showMention]);

  const applyAgentMention = () => {
    if (!mention) return;
    const { next, nextCaret } = insertMentionToken(draft, mention, "agent");
    pendingCaretRef.current = nextCaret;
    setMentionOpen(false);
    onDraftChange(next);
  };

  const applyMemberMention = (member: RoomPresenceMember) => {
    if (!mention) return;
    const { next, nextCaret } = insertMentionToken(draft, mention, member.displayName.trim());
    pendingCaretRef.current = nextCaret;
    setMentionOpen(false);
    onDraftChange(next);
  };

  const applyActiveMention = () => {
    if (!showMention || !mention) return;
    if (showAgent && activeIndex === 0) {
      applyAgentMention();
      return;
    }
    const peopleIndex = showAgent ? activeIndex - 1 : activeIndex;
    const member = filteredPeople[peopleIndex];
    if (member) applyMemberMention(member);
  };

  const insertEmoji = (emoji: string) => {
    const el = textareaRef.current;
    const start = el?.selectionStart ?? caret;
    const end = el?.selectionEnd ?? caret;
    const next = draft.slice(0, start) + emoji + draft.slice(end);
    const nextCaret = start + emoji.length;
    pendingCaretRef.current = nextCaret;
    onDraftChange(next);
    setCaret(nextCaret);
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
          <div
            ref={mentionRef}
            className="absolute right-0 bottom-full left-0 z-10 mb-2 max-h-64 overflow-y-auto rounded-2xl border border-slate-200 bg-white shadow-lg dark:border-zinc-700 dark:bg-[#27272a]"
          >
            {showAgent ? (
              <button
                type="button"
                className={[
                  "flex w-full items-center gap-3 px-4 py-3 text-left transition",
                  activeIndex === 0
                    ? "bg-sky-50 dark:bg-sky-500/10"
                    : "hover:bg-sky-50 dark:hover:bg-sky-500/10",
                ].join(" ")}
                onMouseDown={(e) => {
                  e.preventDefault();
                  applyAgentMention();
                }}
                onMouseEnter={() => setActiveIndex(0)}
              >
                <AthlonIcon className="h-9 w-9 shrink-0 rounded-2xl shadow-sm" alt="" />
                <span className="min-w-0">
                  <span className="block text-sm font-medium text-slate-900 dark:text-zinc-100">@agent</span>
                  <span className="block text-xs text-slate-500 dark:text-zinc-400">Athlon 助手</span>
                </span>
              </button>
            ) : null}
            {filteredPeople.map((member, index) => {
              const optionIndex = (showAgent ? 1 : 0) + index;
              const initial = (member.displayName.trim()[0] || "?").toUpperCase();
              return (
                <button
                  key={member.senderId}
                  type="button"
                  className={[
                    "flex w-full items-center gap-3 px-4 py-3 text-left transition",
                    activeIndex === optionIndex
                      ? "bg-sky-50 dark:bg-sky-500/10"
                      : "hover:bg-sky-50 dark:hover:bg-sky-500/10",
                  ].join(" ")}
                  onMouseDown={(e) => {
                    e.preventDefault();
                    applyMemberMention(member);
                  }}
                  onMouseEnter={() => setActiveIndex(optionIndex)}
                >
                  <span
                    className="flex h-9 w-9 shrink-0 items-center justify-center rounded-2xl text-sm font-semibold text-white shadow-sm"
                    style={{ background: avatarColor(member.senderId) }}
                  >
                    {initial}
                  </span>
                  <span className="min-w-0">
                    <span className="block truncate text-sm font-medium text-slate-900 dark:text-zinc-100">
                      @{member.displayName}
                    </span>
                    <span className="block text-xs text-slate-500 dark:text-zinc-400">团队成员</span>
                  </span>
                </button>
              );
            })}
          </div>
        ) : null}

        {emojiOpen ? (
          <div
            ref={emojiRef}
            className="absolute right-0 bottom-full left-0 z-20 mb-2 max-h-56 overflow-y-auto rounded-2xl border border-slate-200 bg-white p-3 shadow-lg dark:border-zinc-700 dark:bg-[#27272a]"
          >
            {EMOJI_GROUPS.map((group) => (
              <div key={group.label} className="mb-2 last:mb-0">
                <div className="mb-1.5 px-0.5 text-[11px] font-medium text-slate-400 dark:text-zinc-500">
                  {group.label}
                </div>
                <div className="grid grid-cols-10 gap-0.5">
                  {group.emojis.map((emoji) => (
                    <button
                      key={`${group.label}-${emoji}`}
                      type="button"
                      className="flex h-8 w-full items-center justify-center rounded-lg text-lg transition hover:bg-sky-50 dark:hover:bg-sky-500/15"
                      onMouseDown={(e) => {
                        e.preventDefault();
                        insertEmoji(emoji);
                      }}
                      title={emoji}
                    >
                      {emoji}
                    </button>
                  ))}
                </div>
              </div>
            ))}
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
          <button
            type="button"
            className={`mb-0.5 rounded-xl p-2 transition ${
              emojiOpen
                ? "bg-white text-sky-700 dark:bg-zinc-800 dark:text-sky-300"
                : "text-slate-500 hover:bg-white hover:text-sky-700 dark:text-zinc-400 dark:hover:bg-zinc-800 dark:hover:text-sky-300"
            }`}
            onClick={() => {
              setEmojiOpen((v) => !v);
              setMentionOpen(false);
            }}
            title="表情"
            aria-label="插入表情"
            disabled={sending}
          >
            <Smile size={20} />
          </button>
          <textarea
            ref={textareaRef}
            className="max-h-28 min-h-[42px] flex-1 resize-none bg-transparent px-2 py-2.5 text-[15px] text-slate-900 outline-none placeholder:text-slate-400 dark:text-zinc-100 dark:placeholder:text-zinc-500"
            rows={1}
            placeholder="输入消息，可粘贴图片；@ 可提及成员或召唤助手"
            value={draft}
            disabled={sending}
            onChange={(e) => {
              onDraftChange(e.target.value);
              setCaret(e.target.selectionStart);
            }}
            onClick={syncCaret}
            onKeyUp={syncCaret}
            onSelect={syncCaret}
            onFocus={() => setEmojiOpen(false)}
            onPaste={(e) => {
              const images = extractClipboardImages(e.clipboardData);
              if (images.length === 0) return;
              e.preventDefault();
              onPickFiles(images);
            }}
            onKeyDown={(e) => {
              if (showMention && (e.key === "ArrowDown" || e.key === "ArrowUp")) {
                e.preventDefault();
                setActiveIndex((prev) => {
                  if (optionsCount <= 0) return 0;
                  if (e.key === "ArrowDown") return (prev + 1) % optionsCount;
                  return (prev - 1 + optionsCount) % optionsCount;
                });
                return;
              }
              if (showMention && (e.key === "Enter" || e.key === "Tab")) {
                e.preventDefault();
                applyActiveMention();
                return;
              }
              if (showMention && e.key === "Escape") {
                e.preventDefault();
                setMentionOpen(false);
                return;
              }
              if (emojiOpen && e.key === "Escape") {
                e.preventDefault();
                setEmojiOpen(false);
                return;
              }
              if (e.key === "Enter" && !e.shiftKey) {
                e.preventDefault();
                setEmojiOpen(false);
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
