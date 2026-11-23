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

export function ComposerBar({
  draft,
  pending,
  sending,
  onDraftChange,
  onSend,
  onPickFiles,
  onRemovePending,
}: ComposerBarProps) {
  return (
    <div
      className="border-t border-white/10 bg-[var(--composer-bg)] px-3 pt-2 pb-[max(0.75rem,env(safe-area-inset-bottom))]"
      onDragOver={(e) => e.preventDefault()}
      onDrop={(e) => {
        e.preventDefault();
        onPickFiles(e.dataTransfer.files);
      }}
    >
      {pending.length > 0 ? (
        <div className="mb-2 flex gap-2 overflow-x-auto pb-1">
          {pending.map((p) => (
            <div
              key={p.id}
              className="relative flex shrink-0 items-center gap-2 rounded-xl border border-white/10 bg-[#2a2a2a] px-2 py-1.5"
            >
              {p.previewUrl ? (
                <img src={p.previewUrl} alt="" className="h-12 w-12 rounded-lg object-cover" />
              ) : (
                <div className="flex h-12 w-12 items-center justify-center rounded-lg bg-white/10 text-xs text-[var(--text-muted)]">
                  文件
                </div>
              )}
              <div className="max-w-[120px]">
                <div className="truncate text-xs font-medium text-[var(--text-primary)]">{p.file.name}</div>
                <div className="text-[10px] text-[var(--text-muted)]">{formatBytes(p.file.size)}</div>
              </div>
              <button
                type="button"
                className="absolute -top-1.5 -right-1.5 rounded-full bg-[#555] p-0.5 text-white"
                onClick={() => onRemovePending(p.id)}
                aria-label="移除"
              >
                <X size={12} />
              </button>
            </div>
          ))}
        </div>
      ) : null}

      <div className="flex items-end gap-2">
        <label className="mb-1 cursor-pointer rounded-full p-2 text-[var(--text-muted)] hover:bg-white/5">
          <ImagePlus size={22} />
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
        <label className="mb-1 cursor-pointer rounded-full p-2 text-[var(--text-muted)] hover:bg-white/5">
          <Paperclip size={22} />
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
          className="max-h-28 min-h-[42px] flex-1 resize-none rounded-2xl border border-white/10 bg-[#2c2c2c] px-3 py-2.5 text-[15px] text-[var(--text-primary)] outline-none placeholder:text-[var(--text-muted)] focus:border-[#12b7f5]/60"
          rows={1}
          placeholder="输入消息，@agent 可召唤助手"
          value={draft}
          disabled={sending}
          onChange={(e) => onDraftChange(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter" && !e.shiftKey) {
              e.preventDefault();
              onSend();
            }
          }}
        />
        <button
          type="button"
          className="mb-1 flex h-10 w-10 items-center justify-center rounded-full bg-[#12b7f5] text-white disabled:opacity-40"
          disabled={sending || (!draft.trim() && pending.length === 0)}
          onClick={onSend}
          aria-label="发送"
        >
          <Send size={18} />
        </button>
      </div>
    </div>
  );
}
