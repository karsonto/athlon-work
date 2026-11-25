import { useEffect } from "react";
import { X } from "lucide-react";

export interface MentionToastItem {
  id: string;
  roomKey: string;
  roomTitle: string;
  fromName: string;
  body: string;
}

interface MentionToastProps {
  item: MentionToastItem | null;
  onClose: () => void;
  onOpen: () => void;
}

/** Browser-notification-style toast for @mentions. */
export function MentionToast({ item, onClose, onOpen }: MentionToastProps) {
  useEffect(() => {
    if (!item) return;
    const timer = window.setTimeout(onClose, 6000);
    return () => window.clearTimeout(timer);
  }, [item, onClose]);

  if (!item) return null;

  return (
    <div className="pointer-events-none fixed top-4 right-4 z-[80] w-[min(22rem,calc(100vw-2rem))]">
      <button
        type="button"
        className="pointer-events-auto flex w-full gap-3 rounded-2xl border border-slate-200/90 bg-white/95 p-3.5 text-left shadow-xl shadow-slate-900/10 ring-1 ring-black/5 backdrop-blur-md transition hover:bg-white dark:border-zinc-700 dark:bg-[#1f1f22]/95 dark:shadow-black/40 dark:ring-white/5 dark:hover:bg-[#27272a]"
        onClick={onOpen}
      >
        <div className="flex h-10 w-10 shrink-0 items-center justify-center rounded-full bg-sky-100 text-sm font-semibold text-sky-700 dark:bg-sky-500/20 dark:text-sky-300">
          {(item.fromName.trim()[0] || "@").toUpperCase()}
        </div>
        <div className="min-w-0 flex-1">
          <div className="flex items-start justify-between gap-2">
            <div className="min-w-0">
              <div className="truncate text-sm font-semibold text-slate-900 dark:text-zinc-100">
                {item.roomTitle}
              </div>
              <div className="truncate text-xs text-slate-500 dark:text-zinc-400">
                {item.fromName} @了你
              </div>
            </div>
            <span
              role="button"
              tabIndex={0}
              className="shrink-0 rounded-lg p-1 text-slate-400 transition hover:bg-slate-100 hover:text-slate-700 dark:hover:bg-zinc-800 dark:hover:text-zinc-200"
              onClick={(e) => {
                e.stopPropagation();
                onClose();
              }}
              onKeyDown={(e) => {
                if (e.key === "Enter" || e.key === " ") {
                  e.preventDefault();
                  e.stopPropagation();
                  onClose();
                }
              }}
              aria-label="关闭"
            >
              <X size={14} />
            </span>
          </div>
          <p className="mt-1 line-clamp-2 text-sm leading-5 text-slate-700 dark:text-zinc-300">
            {item.body}
          </p>
        </div>
      </button>
    </div>
  );
}
