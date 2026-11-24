import { useEffect, useMemo, useRef, useState } from "react";
import type { RefObject } from "react";
import { ChevronRight, FileImage, FileText, User, X } from "lucide-react";
import { EXAMPLE_PROMPTS } from "../lib/config";
import type { ChatMessage, FileAttachment, ViewMode } from "../types";
import { AthlonIcon } from "./AthlonIcon";
import { MarkdownContent } from "./MarkdownContent";

interface ChatPaneProps {
  viewMode: ViewMode;
  messages: ChatMessage[];
  isSending: boolean;
  onCopyNotice: (message: string) => void;
  onUseExample: (prompt: string) => void;
  onStartConversation: () => void;
  scrollRef?: RefObject<HTMLDivElement | null>;
}

function buildPreviewDocument(rawHtml: string) {
  if (/<html[\s>]|<!doctype/i.test(rawHtml)) {
    return rawHtml;
  }

  return `<!doctype html>
<html lang="zh-CN">
  <head>
    <meta charset="UTF-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <title>HTML 预览</title>
    <style>
      body {
        margin: 0;
        min-height: 100vh;
        background: #ffffff;
      }
    </style>
  </head>
  <body>
${rawHtml}
  </body>
</html>`;
}

function formatResponseDuration(ms: number): string {
  if (!Number.isFinite(ms) || ms < 0) {
    return "—";
  }
  if (ms < 1000) {
    return `${Math.max(0, Math.round(ms))} ms`;
  }
  const seconds = ms / 1000;
  if (seconds < 60) {
    return `${seconds.toFixed(1)} 秒`;
  }
  const minutes = Math.floor(seconds / 60);
  const rest = Math.round(seconds % 60);
  return `${minutes} 分 ${rest} 秒`;
}

function AssistantReasoningBlock({ reasoning, isLive }: { reasoning: string; isLive: boolean }) {
  const wasLiveRef = useRef(false);
  const [open, setOpen] = useState(isLive);

  useEffect(() => {
    if (isLive) {
      setOpen(true);
    } else if (wasLiveRef.current) {
      setOpen(false);
    }
    wasLiveRef.current = isLive;
  }, [isLive]);

  return (
    <div className="mb-3 overflow-hidden rounded-2xl border border-violet-200/70 bg-violet-50/50 ring-1 ring-violet-100/60 dark:border-violet-500/25 dark:bg-violet-950/30 dark:ring-violet-500/15">
      <button
        type="button"
        onClick={() => setOpen((value) => !value)}
        className="flex w-full items-center gap-2 px-3 py-2.5 text-left text-xs font-medium text-violet-900 transition hover:bg-violet-100/40 dark:text-violet-100 dark:hover:bg-violet-900/25"
      >
        <ChevronRight
          className={["h-4 w-4 shrink-0 transition-transform", open ? "rotate-90" : ""].join(" ")}
          aria-hidden
        />
        思维链
        {isLive ? (
          <span className="ml-auto rounded-full bg-violet-200/90 px-2 py-0.5 text-[10px] font-normal tabular-nums text-violet-950 dark:bg-violet-500/35 dark:text-violet-50">
            生成中
          </span>
        ) : null}
      </button>
      {open ? (
        <div className="border-t border-violet-200/60 px-3 py-2.5 dark:border-violet-500/20">
          <div className="max-h-72 overflow-y-auto whitespace-pre-wrap break-words text-xs leading-relaxed text-slate-700 dark:text-zinc-300">
            {reasoning}
          </div>
        </div>
      ) : null}
    </div>
  );
}

function getAttachmentLabel(attachment: FileAttachment) {
  return attachment.summary ?? (attachment.kind === "image" ? "图片附件" : "已附带文件内容");
}

function summarizeAttachmentGroups(attachments: FileAttachment[]) {
  const textCount = attachments.filter((attachment) => attachment.kind === "text").length;
  const imageCount = attachments.filter((attachment) => attachment.kind === "image").length;

  const labels: string[] = [];
  if (textCount > 0) {
    labels.push("正文已提取");
  }
  if (imageCount > 0) {
    const hasEmbeddedImages = attachments.some((attachment) => attachment.role === "embedded_image");
    labels.push(hasEmbeddedImages ? `补充图片 ${imageCount} 张` : `页面图片 ${imageCount} 张`);
  }
  return labels;
}

function HomePanel({
  onUseExample,
  onStartConversation,
}: {
  onUseExample: (prompt: string) => void;
  onStartConversation: () => void;
}) {
  return (
    <div className="flex h-full flex-col items-center justify-center px-6 py-10">
      <div className="max-w-3xl text-center">
        <AthlonIcon className="mx-auto mb-6 h-16 w-16 rounded-3xl shadow-md shadow-sky-100" />
        <h2 className="text-3xl font-semibold text-slate-900 dark:text-zinc-100">欢迎使用 Athlon 助手</h2>
        <p className="mt-4 text-base leading-7 text-slate-600 dark:text-zinc-400">
          你可以在这里提问代码问题、上传文件让助手分析、查看并续写历史会话。报表生成器和统计入口已移除，界面聚焦于 AI 编程问答本身。
        </p>

        <div className="mt-8 flex flex-wrap justify-center gap-3">
          <button
            type="button"
            className="rounded-2xl bg-sky-600 px-5 py-3 text-sm font-medium text-white transition hover:bg-sky-700 dark:bg-blue-600 dark:hover:bg-blue-500"
            onClick={onStartConversation}
          >
            开始新对话
          </button>
          {EXAMPLE_PROMPTS.map((item) => (
            <button
              key={item.id}
              type="button"
              className="rounded-2xl border border-slate-200 bg-white px-4 py-3 text-sm text-slate-700 transition hover:border-sky-200 hover:bg-sky-50 hover:text-sky-700 dark:border-zinc-700 dark:bg-zinc-900 dark:text-zinc-300 dark:hover:border-blue-400/40 dark:hover:bg-blue-500/10 dark:hover:text-blue-300"
              onClick={() => onUseExample(item.prompt)}
            >
              {item.label}
            </button>
          ))}
        </div>
      </div>
    </div>
  );
}

export function ChatPane({
  viewMode,
  messages,
  isSending,
  onCopyNotice,
  onUseExample,
  onStartConversation,
  scrollRef,
}: ChatPaneProps) {
  const [previewImage, setPreviewImage] = useState<FileAttachment | null>(null);
  const [previewHtml, setPreviewHtml] = useState<string | null>(null);

  const lastAssistantId = useMemo(() => {
    for (let i = messages.length - 1; i >= 0; i -= 1) {
      if (messages[i].role === "assistant") {
        return messages[i].id;
      }
    }
    return null;
  }, [messages]);

  useEffect(() => {
    if (!previewImage && !previewHtml) {
      return;
    }

    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        setPreviewImage(null);
        setPreviewHtml(null);
      }
    };

    window.addEventListener("keydown", onKeyDown);
    return () => {
      window.removeEventListener("keydown", onKeyDown);
    };
  }, [previewHtml, previewImage]);

  if (viewMode === "home") {
    return (
      <div className="flex-1 w-full overflow-hidden bg-[radial-gradient(circle_at_top,_rgba(56,189,248,0.10),transparent_35%),linear-gradient(to_bottom,_#f8fbff,_#eff6ff)] dark:bg-[radial-gradient(circle_at_top,_rgba(59,130,246,0.12),transparent_35%),linear-gradient(to_bottom,_#111113,_#101012)]">
        <HomePanel
          onUseExample={onUseExample}
          onStartConversation={onStartConversation}
        />
      </div>
    );
  }

  return (
    <div
      ref={scrollRef}
      className="flex-1 h-full w-full min-h-0 overflow-y-scroll bg-[radial-gradient(circle_at_top,_rgba(226,232,240,0.6),transparent_30%),linear-gradient(to_bottom,_#f8fbff,_#f1f5f9)] py-6 pl-5 pr-1 dark:bg-[radial-gradient(circle_at_top,_rgba(63,63,70,0.28),transparent_30%),linear-gradient(to_bottom,_#111113,_#101012)]"
    >
      <div className="flex w-full flex-col gap-4 pb-6 pr-4">
        {messages.map((message) => {
          const isUser = message.role === "user";
          const isStreamingAssistant = Boolean(
            !isUser && isSending && message.id === lastAssistantId,
          );
          const imageAttachments = message.attachments?.filter((attachment) => attachment.kind === "image") ?? [];
          const textAttachments = message.attachments?.filter((attachment) => attachment.kind === "text") ?? [];
          const hasUserBody = message.content.trim().length > 0;
          return (
            <div
              key={message.id}
              className={[
                "flex gap-3",
                  isUser ? "justify-end" : "justify-start",
              ].join(" ")}
            >
              {isUser ? (
                <div className="mt-1 flex h-10 w-10 shrink-0 items-center justify-center rounded-2xl bg-sky-600 text-white dark:bg-blue-600">
                  <User size={18} />
                </div>
              ) : null}

              {!isUser ? (
                <AthlonIcon className="mt-1 h-10 w-10 shrink-0 rounded-2xl shadow-sm" />
              ) : null}

              <div
                className={[
                  "max-w-[85%] rounded-3xl px-5 py-4 shadow-sm ring-1",
                  isUser
                    ? "bg-sky-600 text-white ring-sky-500 dark:bg-blue-600 dark:ring-blue-500/60"
                    : "bg-white text-slate-800 ring-slate-200 dark:bg-[#262628] dark:text-zinc-100 dark:ring-zinc-700",
                ].join(" ")}
              >
                <div className="mb-2 flex items-center gap-2 text-sm font-medium">
                  {isUser ? (
                    <User size={16} />
                  ) : (
                    <AthlonIcon className="h-4 w-4 rounded-md" alt="" />
                  )}
                  <span>{isUser ? "您" : "Athlon 助手"}</span>
                </div>
                {isUser ? (
                  <div className="space-y-3">
                    {hasUserBody ? (
                      <div className="whitespace-pre-wrap break-words text-sm leading-7">
                        {message.content}
                      </div>
                    ) : null}

                    {imageAttachments.length > 0 ? (
                      <div className="grid gap-3 sm:grid-cols-2">
                        {imageAttachments.map((attachment) => (
                          <div
                            key={attachment.id}
                            className="overflow-hidden rounded-2xl bg-white/15 ring-1 ring-white/20"
                          >
                            <button
                              type="button"
                              className="block w-full cursor-zoom-in overflow-hidden"
                              onClick={() => setPreviewImage(attachment)}
                              aria-label={`查看图片 ${attachment.name}`}
                            >
                              <img
                                src={attachment.content}
                                alt={attachment.name}
                                className="max-h-64 w-full object-cover transition duration-200 hover:scale-[1.02]"
                              />
                            </button>
                            <div className="flex items-center gap-2 px-3 py-2 text-xs text-sky-50">
                              <FileImage size={14} />
                              <span className="truncate">{attachment.sourceFileName ?? attachment.name}</span>
                              <span className="text-sky-100/80">{getAttachmentLabel(attachment)}</span>
                            </div>
                          </div>
                        ))}
                      </div>
                    ) : null}

                    {textAttachments.length > 0 ? (
                      <div className="flex flex-wrap gap-2">
                        {textAttachments.map((attachment) => (
                          <div
                            key={attachment.id}
                            className="inline-flex items-center gap-2 rounded-full bg-white/15 px-3 py-1.5 text-xs text-sky-50 ring-1 ring-white/20"
                          >
                            <FileText size={13} />
                            <span className="max-w-56 truncate">{attachment.sourceFileName ?? attachment.name}</span>
                            <span className="text-[11px] text-sky-100/80">
                              {getAttachmentLabel(attachment)}
                            </span>
                          </div>
                        ))}
                      </div>
                    ) : null}

                    {!hasUserBody && message.attachments?.length ? (
                      <div className="text-xs text-sky-100/85">
                        {summarizeAttachmentGroups(message.attachments).join("，") || `已上传 ${message.attachments.length} 个附件`}
                      </div>
                    ) : null}
                  </div>
                ) : (
                  <>
                    {message.reasoning?.trim() ? (
                      <AssistantReasoningBlock
                        reasoning={message.reasoning}
                        isLive={isStreamingAssistant}
                      />
                    ) : null}
                    <MarkdownContent
                      content={
                        message.content ||
                        (isStreamingAssistant ? "正在思考中..." : "")
                      }
                      onCopy={onCopyNotice}
                      onPreviewHtml={setPreviewHtml}
                    />
                    {message.responseTimeMs != null && !isStreamingAssistant ? (
                      <div className="mt-3 border-t border-slate-100 pt-2 text-xs tabular-nums text-slate-400 dark:border-zinc-700/80 dark:text-zinc-500">
                        响应耗时 {formatResponseDuration(message.responseTimeMs)}
                      </div>
                    ) : null}
                  </>
                )}
              </div>

            </div>
          );
        })}
      </div>
      {previewImage ? (
        <div
          className="fixed inset-0 z-[70] flex items-center justify-center bg-slate-950/85 p-4 backdrop-blur-sm"
          onClick={() => setPreviewImage(null)}
        >
          <button
            type="button"
            className="absolute right-4 top-4 inline-flex h-11 w-11 items-center justify-center rounded-full bg-slate-900/80 text-white transition hover:bg-slate-800"
            onClick={() => setPreviewImage(null)}
            aria-label="关闭图片预览"
          >
            <X size={20} />
          </button>
          <div
            className="flex max-h-full max-w-[min(96vw,1400px)] flex-col overflow-hidden rounded-3xl border border-white/10 bg-slate-900/80 shadow-2xl"
            onClick={(event) => event.stopPropagation()}
          >
            <div className="flex items-center justify-between gap-3 border-b border-white/10 px-4 py-3 text-sm text-slate-200">
              <div className="min-w-0">
                <div className="truncate font-medium">{previewImage.name}</div>
                <div className="text-xs text-slate-400">
                  {(previewImage.size / 1024).toFixed(2)} KB
                </div>
              </div>
            </div>
            <div className="flex items-center justify-center overflow-auto p-4">
              <img
                src={previewImage.content}
                alt={previewImage.name}
                className="h-auto max-h-[82vh] w-auto max-w-full rounded-2xl object-contain"
              />
            </div>
          </div>
        </div>
      ) : null}
      {previewHtml ? (
        <div
          className="fixed inset-0 z-[70] flex items-center justify-center bg-slate-950/85 p-4 backdrop-blur-sm"
          onClick={() => setPreviewHtml(null)}
        >
          <button
            type="button"
            className="absolute right-4 top-4 inline-flex h-11 w-11 items-center justify-center rounded-full bg-slate-900/80 text-white transition hover:bg-slate-800"
            onClick={() => setPreviewHtml(null)}
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
                <div className="truncate font-medium">HTML 预览</div>
                <div className="text-xs text-slate-400">
                  已在沙箱环境中渲染，不会影响当前页面
                </div>
              </div>
            </div>
            <div className="flex-1 overflow-hidden bg-white p-4">
              <iframe
                title="HTML 预览"
                srcDoc={buildPreviewDocument(previewHtml)}
                sandbox="allow-scripts"
                className="h-full w-full rounded-2xl border border-slate-200 bg-white"
              />
            </div>
          </div>
        </div>
      ) : null}
    </div>
  );
}
