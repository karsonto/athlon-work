import { useEffect } from "react";
import { X } from "lucide-react";
import { buildPreviewDocument } from "../lib/htmlPreview";

interface HtmlPreviewModalProps {
  html: string | null;
  title?: string;
  onClose: () => void;
}

export function HtmlPreviewModal({ html, title = "HTML 预览", onClose }: HtmlPreviewModalProps) {
  useEffect(() => {
    if (!html) return;
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        onClose();
      }
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [html, onClose]);

  if (!html) return null;

  return (
    <div
      className="fixed inset-0 z-[70] flex items-center justify-center bg-slate-950/85 p-4 backdrop-blur-sm"
      onClick={onClose}
    >
      <button
        type="button"
        className="absolute right-4 top-4 inline-flex h-11 w-11 items-center justify-center rounded-full bg-slate-900/80 text-white transition hover:bg-slate-800"
        onClick={onClose}
        aria-label="关闭 HTML 预览"
      >
        <X size={20} />
      </button>
      <div
        className="flex h-[88vh] w-full max-w-[min(96vw,1400px)] flex-col overflow-hidden rounded-3xl border border-white/10 bg-slate-900/80 shadow-2xl"
        onClick={(event) => event.stopPropagation()}
      >
        <div className="flex items-center justify-between gap-3 border-b border-white/10 px-4 py-3 text-sm text-slate-200">
          <div className="min-w-0">
            <div className="truncate font-medium">{title}</div>
            <div className="text-xs text-slate-400">已在沙箱环境中渲染，不会影响当前页面</div>
          </div>
        </div>
        <div className="flex-1 overflow-hidden bg-white p-4">
          <iframe
            title="HTML 预览"
            srcDoc={buildPreviewDocument(html)}
            sandbox="allow-scripts"
            className="h-full w-full rounded-2xl border border-slate-200 bg-white"
          />
        </div>
      </div>
    </div>
  );
}
