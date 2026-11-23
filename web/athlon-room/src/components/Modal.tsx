import { useEffect } from "react";
import type { ReactNode } from "react";
import { X } from "lucide-react";

interface ModalProps {
  open: boolean;
  title: string;
  onClose: () => void;
  children: ReactNode;
  footer?: ReactNode;
  wide?: boolean;
}

export function Modal({ open, title, onClose, children, footer, wide }: ModalProps) {
  useEffect(() => {
    if (!open) return;
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") onClose();
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [onClose, open]);

  if (!open) return null;

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-slate-950/50 p-4 backdrop-blur-sm">
      <div
        className={[
          "w-full overflow-hidden rounded-3xl border border-slate-200 bg-white shadow-2xl dark:border-zinc-800 dark:bg-[#1f1f22]",
          wide ? "max-w-2xl" : "max-w-md",
        ].join(" ")}
      >
        <div className="flex items-center justify-between border-b border-slate-200 px-6 py-4 dark:border-zinc-800">
          <h2 className="text-xl font-semibold text-slate-900 dark:text-zinc-100">{title}</h2>
          <button
            type="button"
            className="rounded-full p-2 text-slate-500 transition hover:bg-slate-100 hover:text-slate-900 dark:text-zinc-400 dark:hover:bg-zinc-800 dark:hover:text-zinc-100"
            onClick={onClose}
            aria-label="关闭"
          >
            <X size={18} />
          </button>
        </div>
        <div className="max-h-[70vh] overflow-y-auto px-6 py-5 text-sm leading-6 text-slate-700 dark:text-zinc-300">
          {children}
        </div>
        {footer ? (
          <div className="flex flex-wrap justify-end gap-3 border-t border-slate-200 px-6 py-4 dark:border-zinc-800">
            {footer}
          </div>
        ) : null}
      </div>
    </div>
  );
}
