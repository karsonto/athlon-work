import { Atom, FileImage, FileText, Paperclip, Send, X } from "lucide-react";
import { ACCEPTED_FILE_TYPES } from "../lib/config";
import type { DocumentParseMode, FileAttachment } from "../types";

export interface PendingDocumentItem {
  id: string;
  file: File;
}

interface ComposerProps {
  variant?: "dock" | "hero";
  draft: string;
  attachments: FileAttachment[];
  pendingDocuments: PendingDocumentItem[];
  documentParseMode: DocumentParseMode;
  isSending?: boolean;
  isReadingAttachments?: boolean;
  disabled?: boolean;
  onDraftChange: (value: string) => void;
  onDocumentParseModeChange: (value: DocumentParseMode) => void;
  onSend: () => void;
  onStop: () => void;
  onSelectFiles: (files: FileList | null) => void;
  onRemoveFile: (id: string) => void;
  onRemovePendingDocument: (id: string) => void;
  deepThinkEnabled: boolean;
  onDeepThinkChange: (value: boolean) => void;
}

const DOCUMENT_PARSE_MODE_OPTIONS: Array<{
  value: DocumentParseMode;
  label: string;
  hint: string;
}> = [
  {
    value: "text_only",
    label: "纯文本",
    hint: "只提取正文，速度最快",
  },
  {
    value: "smart_visual",
    label: "智能补图",
    hint: "正文 + 少量关键图片，默认推荐",
  },
  {
    value: "all_visual",
    label: "全部页图",
    hint: "适合扫描件；PDF 最多附 12 页图，DOCX 为全部内嵌图",
  },
];

export function Composer({
  variant = "dock",
  draft,
  attachments,
  pendingDocuments,
  documentParseMode,
  isSending,
  isReadingAttachments,
  disabled,
  onDraftChange,
  onDocumentParseModeChange,
  onSend,
  onStop,
  onSelectFiles,
  onRemoveFile,
  onRemovePendingDocument,
  deepThinkEnabled,
  onDeepThinkChange,
}: ComposerProps) {
  const isHero = variant === "hero";
  const fileInputId = isHero ? "heroFileInput" : "fileInput";
  const showDocumentParseMode = pendingDocuments.length > 0;

  if (isHero) {
    return (
      <div className="w-full">
        <div
          className="mx-auto max-w-4xl rounded-[1.75rem] border border-slate-200 bg-white/95 p-3 shadow-2xl shadow-sky-100/70 ring-1 ring-white/80 dark:border-zinc-700 dark:bg-[#2a2a2d] dark:shadow-none dark:ring-zinc-800"
          onDragOver={(event) => {
            event.preventDefault();
          }}
          onDrop={(event) => {
            event.preventDefault();
            onSelectFiles(event.dataTransfer.files);
          }}
        >
          <input
            id={fileInputId}
            type="file"
            multiple
            accept={ACCEPTED_FILE_TYPES}
            className="hidden"
            onChange={(event) => {
              onSelectFiles(event.target.files);
              event.currentTarget.value = "";
            }}
          />

          {pendingDocuments.length > 0 || attachments.length > 0 ? (
            <div className="mb-3 flex flex-wrap gap-2">
              {pendingDocuments.map((item) => (
                <div
                  key={item.id}
                  className="inline-flex items-center gap-2 rounded-full border border-amber-200/80 bg-amber-50/90 px-3 py-1.5 text-xs text-amber-950 dark:border-amber-400/25 dark:bg-amber-500/10 dark:text-amber-100"
                >
                  <FileText size={13} className="text-amber-700 dark:text-amber-300" />
                  <span className="max-w-44 truncate" title="发送时解析">
                    {item.file.name}
                  </span>
                  <span className="text-[11px] text-amber-800/80 dark:text-amber-200/70">
                    {(item.file.size / 1024).toFixed(2)} KB · 待解析
                  </span>
                  <button
                    type="button"
                    className="rounded-full p-1 text-amber-700/70 transition hover:bg-white hover:text-rose-600 dark:text-amber-300 dark:hover:bg-zinc-800 dark:hover:text-rose-400"
                    onClick={() => onRemovePendingDocument(item.id)}
                    aria-label={`移除 ${item.file.name}`}
                  >
                    <X size={13} />
                  </button>
                </div>
              ))}
              {attachments.map((file) => (
                <div
                  key={file.id}
                  className="inline-flex items-center gap-2 rounded-full border border-slate-200 bg-slate-50 px-3 py-1.5 text-xs text-slate-700 dark:border-zinc-700 dark:bg-zinc-800 dark:text-zinc-300"
                >
                  {file.kind === "image" ? (
                    <FileImage size={13} className="text-slate-400 dark:text-zinc-500" />
                  ) : (
                    <FileText size={13} className="text-slate-400 dark:text-zinc-500" />
                  )}
                  <span className="max-w-44 truncate">{file.sourceFileName ?? file.name}</span>
                  <button
                    type="button"
                    className="rounded-full p-1 text-slate-400 transition hover:bg-white hover:text-rose-600 dark:text-zinc-500 dark:hover:bg-zinc-700 dark:hover:text-rose-400"
                    onClick={() => onRemoveFile(file.id)}
                    aria-label={`移除 ${file.name}`}
                  >
                    <X size={13} />
                  </button>
                </div>
              ))}
            </div>
          ) : null}

          {isReadingAttachments ? (
            <div className="mb-3 text-xs text-slate-500 dark:text-zinc-400">解析中...</div>
          ) : null}

          <textarea
            value={draft}
            onChange={(event) => {
              onDraftChange(event.target.value);
            }}
            onKeyDown={(event) => {
              if (disabled) {
                return;
              }

              if (event.key === "Enter" && !event.shiftKey) {
                event.preventDefault();
                onSend();
              }
            }}
            placeholder="Message Athlon"
            className="h-14 w-full resize-none overflow-y-auto bg-transparent px-1 py-1 text-base leading-7 text-slate-800 outline-none placeholder:text-slate-400 dark:text-zinc-100 dark:placeholder:text-zinc-500"
          />

          <div className="mt-2 flex items-center justify-between gap-3">
            <div className="flex min-w-0 flex-1 flex-wrap items-center gap-2">
              <button
                type="button"
                className={[
                  "inline-flex shrink-0 items-center gap-0.5 rounded-full px-1.5 py-0.5 text-[10px] font-medium ring-1 transition",
                  deepThinkEnabled
                    ? "bg-sky-600 text-white ring-sky-600 shadow-sm dark:bg-violet-950/80 dark:text-violet-100 dark:ring-violet-500/40"
                    : "bg-slate-100/90 text-slate-600 ring-slate-200 hover:bg-slate-200/90 dark:bg-zinc-800 dark:text-zinc-300 dark:ring-zinc-600 dark:hover:bg-zinc-700",
                ].join(" ")}
                aria-pressed={deepThinkEnabled}
                aria-label={deepThinkEnabled ? "关闭 DeepThink" : "开启 DeepThink"}
                title="深度思考：请求携带 enable_thinking，并展示模型思维链"
                onClick={() => onDeepThinkChange(!deepThinkEnabled)}
              >
                <Atom size={10} className="shrink-0 opacity-90" aria-hidden />
                DeepThink
              </button>
              {showDocumentParseMode ? (
                <div className="flex min-w-0 flex-wrap items-center gap-1.5">
                  {DOCUMENT_PARSE_MODE_OPTIONS.map((option) => {
                    const selected = option.value === documentParseMode;
                    return (
                      <button
                        key={option.value}
                        type="button"
                        className={[
                          "rounded-full px-2.5 py-1 text-xs transition ring-1",
                          selected
                            ? "bg-sky-50 text-sky-700 ring-sky-200 dark:bg-blue-500/15 dark:text-blue-300 dark:ring-blue-400/30"
                            : "bg-slate-50 text-slate-600 ring-slate-200 hover:bg-slate-100 hover:text-slate-900 dark:bg-zinc-800 dark:text-zinc-300 dark:ring-zinc-700 dark:hover:bg-zinc-700 dark:hover:text-zinc-100",
                        ].join(" ")}
                        onClick={() => onDocumentParseModeChange(option.value)}
                        aria-pressed={selected}
                      >
                        {option.label}
                      </button>
                    );
                  })}
                </div>
              ) : null}
            </div>

            <div className="flex shrink-0 items-center gap-2">
              <label
                htmlFor={fileInputId}
                className="inline-flex h-10 w-10 cursor-pointer items-center justify-center rounded-full text-slate-500 transition hover:bg-slate-100 hover:text-slate-900 dark:text-zinc-400 dark:hover:bg-zinc-800 dark:hover:text-zinc-100"
                aria-label="添加附件"
                title="添加附件"
              >
                <Paperclip size={18} />
              </label>
              {isSending ? (
                <button
                  type="button"
                  className="inline-flex h-10 w-10 items-center justify-center rounded-full bg-rose-500 text-white transition hover:bg-rose-600"
                  onClick={onStop}
                  aria-label="暂停当前回复"
                  title="暂停当前回复"
                >
                  <span className="h-3 w-3 rounded-[3px] bg-white" aria-hidden="true" />
                </button>
              ) : (
                <button
                  type="button"
                  className="inline-flex h-10 w-10 items-center justify-center rounded-full bg-sky-600 text-white transition hover:bg-sky-700 disabled:cursor-not-allowed disabled:bg-slate-200 disabled:text-slate-400 dark:bg-blue-600 dark:hover:bg-blue-500 dark:disabled:bg-zinc-700 dark:disabled:text-zinc-500"
                  onClick={onSend}
                  disabled={disabled}
                  aria-label="发送"
                >
                  <Send size={16} />
                </button>
              )}
            </div>
          </div>
        </div>
      </div>
    );
  }

  return (
    <div className="border-t border-slate-200 bg-white px-5 py-4 dark:border-zinc-800 dark:bg-[#101012]">
      <div
        className={[
          "mx-auto",
          "max-w-4xl",
        ].join(" ")}
      >
        <label
          htmlFor={fileInputId}
          className={[
            "mb-3 flex cursor-pointer items-center justify-between gap-3 rounded-2xl border border-dashed px-4 py-2.5 text-sm transition",
            isHero
              ? "border-slate-200 bg-slate-50/80 text-slate-600 hover:border-sky-300 hover:bg-sky-50 hover:text-sky-700"
              : "border-slate-200 bg-slate-50/80 text-slate-600 hover:border-sky-300 hover:bg-sky-50 hover:text-sky-700",
          ].join(" ")}
          onDragOver={(event) => {
            event.preventDefault();
          }}
          onDrop={(event) => {
            event.preventDefault();
            onSelectFiles(event.dataTransfer.files);
          }}
        >
          <div className="inline-flex items-center gap-2">
            <span className="flex h-8 w-8 items-center justify-center rounded-xl bg-white text-sky-600 shadow-sm ring-1 ring-slate-200">
              <Paperclip size={15} />
            </span>
            <div className="leading-tight">
              <div className="font-medium text-slate-700">添加附件</div>
              <div className="text-xs text-slate-500">拖拽到此处或点击选择</div>
            </div>
          </div>
          <div className="hidden rounded-full bg-white px-3 py-1 text-xs text-slate-500 ring-1 ring-slate-200 sm:block">
            支持代码、文本、图片、PDF、Word、Excel、CSV、PPTX
          </div>
        </label>

        <input
          id={fileInputId}
          type="file"
          multiple
          accept={ACCEPTED_FILE_TYPES}
          className="hidden"
          onChange={(event) => {
            onSelectFiles(event.target.files);
            event.currentTarget.value = "";
          }}
        />

        {showDocumentParseMode ? (
          <div className="mb-3 flex flex-wrap items-center gap-2 text-xs">
            {DOCUMENT_PARSE_MODE_OPTIONS.map((option) => {
              const selected = option.value === documentParseMode;
              return (
                <button
                  key={option.value}
                  type="button"
                  className={[
                    "rounded-full px-3 py-1.5 transition ring-1",
                    selected
                      ? "bg-sky-600 text-white ring-sky-600"
                      : "bg-white text-slate-600 ring-slate-200 hover:bg-slate-50 hover:text-slate-900",
                  ].join(" ")}
                  onClick={() => onDocumentParseModeChange(option.value)}
                  aria-pressed={selected}
                >
                  {option.label}
                </button>
              );
            })}
            <span className="text-slate-500">
              {DOCUMENT_PARSE_MODE_OPTIONS.find((option) => option.value === documentParseMode)?.hint}
            </span>
          </div>
        ) : null}

        {isReadingAttachments ? (
          <div className="mb-3 rounded-2xl border border-sky-100 bg-sky-50 px-4 py-3 text-sm text-sky-700">
            正在解析附件内容，请稍候...
          </div>
        ) : null}

        {pendingDocuments.length > 0 || attachments.length > 0 ? (
          <div className="mb-3 flex flex-wrap gap-2">
            {pendingDocuments.map((item) => (
              <div
                key={item.id}
                className="inline-flex items-center gap-2 rounded-full border border-amber-200/80 bg-amber-50/90 px-3 py-1.5 text-sm text-amber-950 dark:border-amber-400/25 dark:bg-amber-500/10 dark:text-amber-100"
              >
                <FileText size={13} className="text-amber-700 dark:text-amber-300" />
                <span className="max-w-48 truncate" title="发送时解析">
                  {item.file.name}
                </span>
                <span className="text-xs text-amber-800/80 dark:text-amber-200/70">
                  {(item.file.size / 1024).toFixed(2)} KB · 待解析
                </span>
                <button
                  type="button"
                  className="rounded-full p-1 text-amber-700/70 transition hover:bg-white hover:text-rose-600 dark:text-amber-300 dark:hover:bg-zinc-800 dark:hover:text-rose-400"
                  onClick={() => onRemovePendingDocument(item.id)}
                  aria-label={`移除 ${item.file.name}`}
                >
                  <X size={14} />
                </button>
              </div>
            ))}
            {attachments.map((file) => (
              <div
                key={file.id}
                className="inline-flex items-center gap-2 rounded-full border border-slate-200 bg-slate-50 px-3 py-1.5 text-sm text-slate-700"
              >
                {file.kind === "image" ? (
                  <FileImage size={13} className="text-slate-400" />
                ) : (
                  <FileText size={13} className="text-slate-400" />
                )}
                <span className="max-w-48 truncate">{file.sourceFileName ?? file.name}</span>
                {file.summary ? (
                  <span className="max-w-40 truncate text-xs text-slate-500">
                    {file.summary}
                  </span>
                ) : null}
                <span className="text-xs text-slate-500">
                  {(file.size / 1024).toFixed(2)} KB
                </span>
                <button
                  type="button"
                  className="rounded-full p-1 text-slate-400 transition hover:bg-white hover:text-rose-600"
                  onClick={() => onRemoveFile(file.id)}
                  aria-label={`移除 ${file.name}`}
                >
                  <X size={14} />
                </button>
              </div>
            ))}
          </div>
        ) : null}

        <div
          className={[
            "flex items-end gap-2",
            isHero ? "rounded-3xl bg-slate-50 p-2 ring-1 ring-slate-200 focus-within:bg-white focus-within:ring-sky-200" : "",
          ].join(" ")}
        >
          <button
            type="button"
            className={[
              "mb-1 inline-flex shrink-0 items-center gap-0.5 self-end rounded-full px-1.5 py-1 text-[10px] font-medium ring-1 transition",
              deepThinkEnabled
                ? "bg-sky-600 text-white ring-sky-600 shadow-sm dark:bg-violet-950/80 dark:text-violet-100 dark:ring-violet-500/40"
                : "bg-white text-slate-600 ring-slate-200 hover:bg-slate-50 dark:bg-zinc-800 dark:text-zinc-300 dark:ring-zinc-600 dark:hover:bg-zinc-700",
            ].join(" ")}
            aria-pressed={deepThinkEnabled}
            aria-label={deepThinkEnabled ? "关闭 DeepThink" : "开启 DeepThink"}
            title="深度思考"
            onClick={() => onDeepThinkChange(!deepThinkEnabled)}
          >
            <Atom size={10} className="shrink-0 opacity-90" aria-hidden />
            DeepThink
          </button>
          <div
            className={[
              "flex min-w-0 flex-1 items-end gap-3",
              !isHero ? "rounded-3xl bg-slate-50 p-2 ring-1 ring-slate-200 focus-within:bg-white focus-within:ring-sky-200 dark:bg-zinc-900/50 dark:ring-zinc-700" : "",
            ].join(" ")}
          >
            <textarea
              value={draft}
              onChange={(event) => {
                onDraftChange(event.target.value);
              }}
              onKeyDown={(event) => {
                if (disabled) {
                  return;
                }

                if (event.key === "Enter" && !event.shiftKey) {
                  event.preventDefault();
                  onSend();
                }
              }}
              placeholder="输入你的问题或代码，按 Enter 发送，Shift + Enter 换行"
              className={[
                "min-w-0 flex-1 resize-none overflow-y-auto px-5 py-4 text-sm leading-6 text-slate-800 outline-none transition dark:text-zinc-100",
                isHero
                  ? "h-24 rounded-[1.5rem] border-0 bg-transparent focus:bg-transparent"
                  : "h-20 rounded-3xl border border-slate-200 bg-slate-50 focus:border-sky-300 focus:bg-white focus:ring-4 focus:ring-sky-100 dark:border-zinc-700 dark:bg-zinc-800 dark:focus:border-blue-500 dark:focus:ring-blue-500/20",
              ].join(" ")}
            />
            {isSending ? (
              <button
                type="button"
                className="inline-flex h-[48px] w-[48px] shrink-0 items-center justify-center rounded-full bg-rose-600 text-white shadow-sm ring-1 ring-rose-600/20 transition hover:bg-rose-700 active:scale-[0.98]"
                onClick={onStop}
                aria-label="暂停当前回复"
                title="暂停当前回复"
              >
                <span
                  className="h-3.5 w-3.5 rounded-[3px] bg-white"
                  aria-hidden="true"
                />
              </button>
            ) : (
              <button
                type="button"
                className={[
                  "inline-flex shrink-0 items-center justify-center bg-sky-600 text-sm font-medium text-white transition hover:bg-sky-700 disabled:cursor-not-allowed disabled:bg-slate-300 dark:bg-blue-600 dark:hover:bg-blue-500 dark:disabled:bg-zinc-600",
                  isHero ? "h-[48px] w-[48px] rounded-full" : "h-[52px] gap-2 rounded-3xl px-5 py-3",
                ].join(" ")}
                onClick={onSend}
                disabled={disabled}
                aria-label="发送"
              >
                <Send size={16} />
                {isHero ? null : "发送"}
              </button>
            )}
          </div>
        </div>
      </div>
    </div>
  );
}
