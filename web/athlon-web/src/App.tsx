import { useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { CheckCircle2, PlugZap } from "lucide-react";
import { AppHeader } from "./components/AppHeader";
import { ChatPane } from "./components/ChatPane";
import { Composer } from "./components/Composer";
import { VllmMetricsDashboard } from "./components/VllmMetricsDashboard";
import { EmptyConversation } from "./components/EmptyConversation";
import { Modal } from "./components/Modal";
import { Sidebar } from "./components/Sidebar";
import { isAbortError, isAgentHostedPage, streamChatCompletion } from "./lib/chat";
import { DEFAULT_SETTINGS } from "./lib/config";
import {
  extractAttachments,
  isDocumentParseModeRelevantFileName,
  isLegacyPresentationFileName,
} from "./lib/file-extract";
import { createId } from "./lib/id";
import {
  clearHistoryStorage,
  loadDocumentParseMode,
  clearSettingsStorage,
  loadHistory,
  loadSettings,
  saveDocumentParseMode,
  saveHistory,
  saveSettings,
} from "./lib/storage";
import type {
  AppSettings,
  ChatRequestMessage,
  ChatMessage,
  ConversationHistoryItem,
  DocumentParseMode,
  FileAttachment,
  MessagePart,
  ViewMode,
} from "./types";

const SYSTEM_PROMPT = `
你是 Athlon，由 ICBC AMS 团队搭建的 AI 协作助手，面向团队内部研发、运维、文档和知识分析场景提供支持。

能力定位：
- 可以帮助用户阅读、分析、解释和改写代码、文档、需求说明、技术方案、日志、表格、图片和其他附件内容。
- 可以协助进行问题排查、方案设计、代码评审、文档总结、信息提炼、风险识别和执行步骤整理。
- 你熟悉企业内部协作场景，应重视准确性、安全合规、可维护性和可落地的工程实践。

行为准则：
- 默认使用中文回答，除非用户明确要求其他语言。
- 回答要准确、简洁、可执行，优先给出结论，再补充必要依据。
- 当用户上传或引用文件时，先基于文件内容分析，不要凭空假设。
- 涉及代码时，优先遵循用户项目现有的技术栈、命名、风格和目录结构。
- 涉及文档时，优先提炼结构、关键结论、风险点、待确认事项和可执行建议。
- 对不确定的信息要明确说明不确定，并提出需要补充的上下文。
- 不要展示隐藏推理过程或完整思维链；如需解释，只给出简洁的推理摘要。
- 如果发现潜在 bug、安全风险、合规风险、性能问题、逻辑矛盾或边界条件，应主动指出。
`;

const THEME_STORAGE_KEY = "athlonThemeMode";

function createMessage(
  role: ChatMessage["role"],
  content: string,
  attachments?: FileAttachment[],
  documentParseMode?: DocumentParseMode,
): ChatMessage {
  return {
    id: createId(),
    role,
    content,
    attachments: attachments?.length ? attachments : undefined,
    documentParseMode,
  };
}

function getConversationTitle(messages: ChatMessage[]) {
  const firstUserMessage = messages.find((message) => message.role === "user");
  if (!firstUserMessage) {
    return "新对话";
  }

  if (!firstUserMessage.content.trim() && firstUserMessage.attachments?.length) {
    const primaryName = firstUserMessage.attachments[0].sourceFileName ?? firstUserMessage.attachments[0].name;
    return firstUserMessage.attachments.length === 1
      ? primaryName
      : `${primaryName} 等 ${firstUserMessage.attachments.length} 个附件`;
  }

  const normalized = firstUserMessage.content.replace(/\s+/g, " ").trim();
  return normalized.length > 30 ? `${normalized.slice(0, 30)}...` : normalized;
}

function buildChatRequestMessages(messages: ChatMessage[]): ChatRequestMessage[] {
  return [
    {
      role: "system",
      content: SYSTEM_PROMPT.trim(),
    },
    ...messages.map((message) => ({
      role: message.role,
      content: buildRequestContent(message),
    })),
  ];
}

function getDocumentParseModeLabel(mode: DocumentParseMode | undefined) {
  switch (mode) {
    case "text_only":
      return "纯文本";
    case "smart_visual":
      return "智能补图";
    case "all_visual":
      return "全部页图";
    default:
      return "智能补图";
  }
}

function describeAttachmentOrigin(attachment: FileAttachment) {
  const fileName = attachment.sourceFileName ?? attachment.name;
  const pagePart = attachment.sourcePage ? ` 第 ${attachment.sourcePage} 页` : "";
  return `文件 "${fileName}"${pagePart}`;
}

function formatTextAttachment(attachment: FileAttachment) {
  const label = attachment.summary ?? "提取内容";
  return `${describeAttachmentOrigin(attachment)} 的${label}:\n\`\`\`\n${attachment.content}\n\`\`\``;
}

function formatImageAttachmentSummary(attachment: FileAttachment) {
  const label = attachment.summary ?? "图片补充";
  return `${describeAttachmentOrigin(attachment)} 的${label}`;
}

function buildRequestContent(message: ChatMessage): string | MessagePart[] {
  if (message.role !== "user" || !message.attachments?.length) {
    return message.content;
  }

  const textSegments: string[] = [];
  if (message.content.trim()) {
    textSegments.push(message.content.trim());
  }

  if (message.documentParseMode && message.attachments?.length) {
    textSegments.push(`本次文档解析模式：${getDocumentParseModeLabel(message.documentParseMode)}`);
  }

  const imageParts: MessagePart[] = [];
  const imageSummaries: string[] = [];
  for (const attachment of message.attachments) {
    if (attachment.kind === "image") {
      imageSummaries.push(formatImageAttachmentSummary(attachment));
      imageParts.push({
        type: "image_url",
        image_url: {
          url: attachment.content,
        },
      });
      continue;
    }

    textSegments.push(formatTextAttachment(attachment));
  }

  if (imageSummaries.length > 0) {
    textSegments.push(`附图说明:\n- ${imageSummaries.join("\n- ")}`);
  }

  if (imageParts.length === 0) {
    return textSegments.join("\n\n");
  }

  const parts: MessagePart[] = [];
  if (textSegments.length > 0) {
    parts.push({
      type: "text",
      text: textSegments.join("\n\n"),
    });
  }

  parts.push(...imageParts);
  return parts;
}

function buildStoppedAssistantContent(content: string) {
  const trimmed = content.trimEnd();
  return trimmed ? `${trimmed}\n\n[已暂停]` : "已暂停";
}

async function readAttachmentsWithMode(
  file: File,
  mode: DocumentParseMode,
): Promise<FileAttachment[]> {
  try {
    const extracted = await extractAttachments(file, mode);
    return extracted.map((attachment) => ({
      id: createId(),
      name: attachment.name,
      size: attachment.size,
      type: attachment.type,
      kind: attachment.kind,
      content: attachment.content,
      sourceFileName: attachment.sourceFileName,
      sourcePage: attachment.sourcePage,
      role: attachment.role,
      summary: attachment.summary,
    }));
  } catch (error) {
    const reason = error instanceof Error ? error.message : "未知错误";
    throw new Error(`"${file.name}" 解析失败: ${reason}`);
  }
}

export default function App() {
  const [viewMode, setViewMode] = useState<ViewMode>("home");
  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [history, setHistory] = useState<ConversationHistoryItem[]>(() => loadHistory());
  const [currentHistoryId, setCurrentHistoryId] = useState<string | null>(null);
  const [isDarkMode, setIsDarkMode] = useState(() => {
    try {
      const stored = window.localStorage.getItem(THEME_STORAGE_KEY);
      if (stored === "dark") {
        return true;
      }
      if (stored === "light") {
        return false;
      }
    } catch (error) {
      console.error("加载主题设置失败:", error);
    }

    return window.matchMedia?.("(prefers-color-scheme: dark)").matches ?? false;
  });
  const [settings, setSettings] = useState<AppSettings>(() => loadSettings());
  const [draftSettings, setDraftSettings] = useState<AppSettings>(() => loadSettings());
  const [documentParseMode, setDocumentParseMode] = useState<DocumentParseMode>(() => loadDocumentParseMode());
  const [deepThinkEnabled, setDeepThinkEnabled] = useState(false);
  const [draft, setDraft] = useState("");
  const [attachments, setAttachments] = useState<FileAttachment[]>([]);
  const [pendingDocumentFiles, setPendingDocumentFiles] = useState<Array<{ id: string; file: File }>>(
    [],
  );
  const [isSending, setIsSending] = useState(false);
  const [isReadingAttachments, setIsReadingAttachments] = useState(false);
  const [helpOpen, setHelpOpen] = useState(false);
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [notification, setNotification] = useState("");
  const [isFullscreen, setIsFullscreen] = useState(Boolean(document.fullscreenElement));
  const notificationTimerRef = useRef<number | null>(null);
  const viewportRef = useRef<HTMLDivElement | null>(null);
  const activeRequestControllerRef = useRef<AbortController | null>(null);

  useEffect(() => {
    document.documentElement.classList.toggle("dark", isDarkMode);
    document.documentElement.setAttribute("data-theme", isDarkMode ? "dark" : "light");
    try {
      window.localStorage.setItem(THEME_STORAGE_KEY, isDarkMode ? "dark" : "light");
    } catch (error) {
      console.error("保存主题设置失败:", error);
    }
  }, [isDarkMode]);

  const scrollChatToBottom = () => {
    const viewport = viewportRef.current;
    if (!viewport) {
      return;
    }

    viewport.scrollTo({
      top: viewport.scrollHeight,
      behavior: "auto",
    });
  };

  useEffect(() => {
    const onFullscreenChange = () => {
      setIsFullscreen(Boolean(document.fullscreenElement));
    };

    document.addEventListener("fullscreenchange", onFullscreenChange);
    return () => {
      document.removeEventListener("fullscreenchange", onFullscreenChange);
    };
  }, []);

  useLayoutEffect(() => {
    if (!viewportRef.current || viewMode !== "chat") {
      return;
    }

    scrollChatToBottom();
    const frameId = window.requestAnimationFrame(() => {
      scrollChatToBottom();
    });
    const timeoutId = window.setTimeout(() => {
      scrollChatToBottom();
    }, 80);

    return () => {
      window.cancelAnimationFrame(frameId);
      window.clearTimeout(timeoutId);
    };
  }, [messages, viewMode]);

  useEffect(() => {
    return () => {
      if (notificationTimerRef.current) {
        window.clearTimeout(notificationTimerRef.current);
      }
    };
  }, []);

  const canSend = useMemo(() => {
    return (
      !isSending &&
      !isReadingAttachments &&
      (draft.trim().length > 0 || attachments.length > 0 || pendingDocumentFiles.length > 0)
    );
  }, [attachments.length, draft, isReadingAttachments, isSending, pendingDocumentFiles.length]);
  const hasUserMessage = useMemo(() => {
    return messages.some((message) => message.role === "user");
  }, [messages]);
  const isEmptyConversation = viewMode === "chat" && currentHistoryId === null && !hasUserMessage;

  const showNotification = (message: string) => {
    setNotification(message);
    if (notificationTimerRef.current) {
      window.clearTimeout(notificationTimerRef.current);
    }
    notificationTimerRef.current = window.setTimeout(() => {
      setNotification("");
    }, 2500);
  };

  const persistConversation = (
    nextMessages: ChatMessage[],
    options?: { keepCurrentId?: boolean },
  ) => {
    if (!nextMessages.some((message) => message.role === "user")) {
      return;
    }

    const historyId = options?.keepCurrentId && currentHistoryId
      ? currentHistoryId
      : currentHistoryId ?? createId();

    const item: ConversationHistoryItem = {
      id: historyId,
      title: getConversationTitle(nextMessages),
      messages: nextMessages,
      timestamp: new Date().toISOString(),
    };

    setCurrentHistoryId(historyId);
    setHistory((prev) => {
      const existingIndex = prev.findIndex((entry) => entry.id === historyId);
      const next = [...prev];

      if (existingIndex >= 0) {
        next.splice(existingIndex, 1);
      }

      next.unshift(item);
      const limited = next.slice(0, 5);
      if (!saveHistory(limited)) {
        showNotification("历史记录保存失败，可能是附件内容过大");
      }
      return limited;
    });
  };

  const resetComposer = () => {
    setDraft("");
    setAttachments([]);
    setPendingDocumentFiles([]);
  };

  const openNewConversation = (prefill?: string) => {
    if (messages.length > 0 && viewMode === "chat") {
      persistConversation(messages, { keepCurrentId: true });
    }

    setViewMode("chat");
    setCurrentHistoryId(null);
    setMessages([]);
    setDraft(prefill ?? "");
    setAttachments([]);
    setPendingDocumentFiles([]);
  };

  const handleShowHome = () => {
    if (messages.length > 0 && viewMode === "chat") {
      persistConversation(messages, { keepCurrentId: true });
    }
    setViewMode("home");
  };

  const handleUseExample = (prompt: string) => {
    if (viewMode === "home") {
      openNewConversation(prompt);
      return;
    }

    setViewMode("chat");
    setDraft(prompt);
  };

  const handleDocumentParseModeChange = (value: DocumentParseMode) => {
    setDocumentParseMode(value);
    if (!saveDocumentParseMode(value)) {
      showNotification("解析模式保存失败");
    }
  };

  const handleSelectFiles = async (fileList: FileList | null) => {
    if (!fileList || fileList.length === 0) {
      return;
    }

    const selectedFiles = Array.from(fileList);
    const pendingDocItems: Array<{ id: string; file: File }> = [];
    const filesToParseNow: File[] = [];

    for (const file of selectedFiles) {
      if (isLegacyPresentationFileName(file.name)) {
        showNotification(
          `"${file.name}"：暂不支持老式 ".ppt" 文件，请另存为 ".pptx" 后上传`,
        );
        continue;
      }
      if (isDocumentParseModeRelevantFileName(file.name)) {
        pendingDocItems.push({ id: createId(), file });
      } else {
        filesToParseNow.push(file);
      }
    }

    if (pendingDocItems.length > 0) {
      setPendingDocumentFiles((prev) => [...prev, ...pendingDocItems]);
      showNotification(
        pendingDocItems.length === 1
          ? `已添加文档「${pendingDocItems[0].file.name}」，发送时将按所选模式解析`
          : `已添加 ${pendingDocItems.length} 个文档，发送时将按所选模式解析`,
      );
    }

    if (filesToParseNow.length === 0) {
      return;
    }

    setIsReadingAttachments(true);

    try {
      const results = await Promise.allSettled(
        filesToParseNow.map((file) => readAttachmentsWithMode(file, documentParseMode)),
      );

      const nextAttachments = results
        .filter((result): result is PromiseFulfilledResult<FileAttachment[]> => result.status === "fulfilled")
        .flatMap((result) => result.value);

      const failedResults = results.filter(
        (result): result is PromiseRejectedResult => result.status === "rejected",
      );
      const failedCount = failedResults.length;
      const firstFailureMessage = failedResults[0]?.reason instanceof Error
        ? failedResults[0].reason.message
        : "附件解析失败";

      if (nextAttachments.length > 0) {
        setAttachments((prev) => [...prev, ...nextAttachments]);
      }

      if (failedCount > 0) {
        showNotification(
          failedCount === filesToParseNow.length
            ? firstFailureMessage
            : `已添加 ${nextAttachments.length} 个附件，另有 ${failedCount} 个解析失败：${firstFailureMessage}`,
        );
        return;
      }

      showNotification(`已添加 ${nextAttachments.length} 个附件`);
    } finally {
      setIsReadingAttachments(false);
    }
  };

  const handleLoadConversation = (id: string) => {
    const conversation = history.find((item) => item.id === id);
    if (!conversation) {
      return;
    }

    setViewMode("chat");
    setCurrentHistoryId(conversation.id);
    setMessages(conversation.messages);
    resetComposer();
  };

  const handleDeleteHistory = (id: string) => {
    if (!window.confirm("确定要删除这条历史记录吗？")) {
      return;
    }

    setHistory((prev) => {
      const next = prev.filter((item) => item.id !== id);
      if (!saveHistory(next)) {
        showNotification("历史记录保存失败");
      }
      return next;
    });

    if (currentHistoryId === id) {
      setCurrentHistoryId(null);
      setMessages([]);
      setViewMode("home");
    }
  };

  const handleClearHistory = () => {
    if (!window.confirm("确定要清空所有历史记录吗？此操作无法撤销。")) {
      return;
    }

    setHistory([]);
    setCurrentHistoryId(null);
    setMessages([]);
    setViewMode("home");
    clearHistoryStorage();
    showNotification("历史记录已清空");
  };

  const handleStop = () => {
    if (!isSending || !activeRequestControllerRef.current) {
      return;
    }

    activeRequestControllerRef.current.abort();
    showNotification("已暂停当前回复");
  };

  const handleSend = async () => {
    if (isSending || isReadingAttachments) {
      return;
    }

    const trimmedDraft = draft.trim();
    const currentAttachments = attachments;
    const currentPending = pendingDocumentFiles;

    if (!trimmedDraft && currentAttachments.length === 0 && currentPending.length === 0) {
      return;
    }

    let mergedAttachments = [...currentAttachments];
    const hadPendingDocs = currentPending.length > 0;

    if (hadPendingDocs) {
      setIsReadingAttachments(true);
      try {
        const results = await Promise.allSettled(
          currentPending.map((item) => readAttachmentsWithMode(item.file, documentParseMode)),
        );

        const fulfilled = results
          .filter((result): result is PromiseFulfilledResult<FileAttachment[]> => result.status === "fulfilled")
          .flatMap((result) => result.value);

        const failed = results.filter(
          (result): result is PromiseRejectedResult => result.status === "rejected",
        );

        if (failed.length > 0) {
          const firstFailureMessage = failed[0].reason instanceof Error
            ? failed[0].reason.message
            : "附件解析失败";
          if (fulfilled.length === 0) {
            showNotification(firstFailureMessage);
            return;
          }
          showNotification(
            `已解析 ${fulfilled.length} 项内容，另有 ${failed.length} 个文档失败：${firstFailureMessage}`,
          );
        }

        mergedAttachments = [...currentAttachments, ...fulfilled];
        setPendingDocumentFiles([]);
      } finally {
        setIsReadingAttachments(false);
      }
    }

    if (!trimmedDraft && mergedAttachments.length === 0) {
      return;
    }

    if (viewMode === "home") {
      openNewConversation();
    }

    const outgoingMessages: ChatMessage[] = [];
    if (trimmedDraft || mergedAttachments.length > 0) {
      outgoingMessages.push(
        createMessage(
          "user",
          trimmedDraft,
          mergedAttachments,
          hadPendingDocs ? documentParseMode : undefined,
        ),
      );
    }

    const assistantMessage = createMessage("assistant", "");
    const previousMessages = messages;
    const requestMessages = [...previousMessages, ...outgoingMessages];
    const nextMessages = [...requestMessages, assistantMessage];
    const controller = new AbortController();

    activeRequestControllerRef.current = controller;

    setViewMode("chat");
    setMessages(nextMessages);
    resetComposer();
    setIsSending(true);

    const enableThinkingSnapshot = deepThinkEnabled;
    let assistantContent = "";
    let assistantReasoning = "";
    const responseStartedAt = performance.now();

    try {
      await streamChatCompletion(
        settings,
        buildChatRequestMessages(requestMessages),
        {
          onChunk: (chunk) => {
            assistantContent += chunk;
            setMessages((prev) =>
              prev.map((message) =>
                message.id === assistantMessage.id
                  ? { ...message, content: assistantContent, reasoning: assistantReasoning || undefined }
                  : message,
              ),
            );
          },
          onReasoningChunk: (chunk) => {
            assistantReasoning += chunk;
            setMessages((prev) =>
              prev.map((message) =>
                message.id === assistantMessage.id
                  ? { ...message, reasoning: assistantReasoning, content: assistantContent }
                  : message,
              ),
            );
          },
        },
        controller.signal,
        enableThinkingSnapshot,
      );

      const responseTimeMs = Math.round(performance.now() - responseStartedAt);
      const finalMessages = requestMessages.concat({
        ...assistantMessage,
        content: assistantContent,
        reasoning: assistantReasoning.trim() ? assistantReasoning : undefined,
        responseTimeMs,
      });
      setMessages(finalMessages);
      persistConversation(finalMessages, { keepCurrentId: true });
    } catch (error) {
      const responseTimeMs = Math.round(performance.now() - responseStartedAt);
      if (isAbortError(error)) {
        const finalMessages = requestMessages.concat({
          ...assistantMessage,
          content: buildStoppedAssistantContent(assistantContent),
          reasoning: assistantReasoning.trim() ? assistantReasoning : undefined,
          responseTimeMs,
        });
        setMessages(finalMessages);
        persistConversation(finalMessages, { keepCurrentId: true });
        return;
      }

      console.error("请求失败:", error);
      const errorText = error instanceof Error ? error.message : "未知错误";
      const finalMessages = requestMessages.concat({
        ...assistantMessage,
        content: `请求失败: ${errorText}`,
        reasoning: assistantReasoning.trim() ? assistantReasoning : undefined,
        responseTimeMs,
      });
      setMessages(finalMessages);
      persistConversation(finalMessages, { keepCurrentId: true });
    } finally {
      if (activeRequestControllerRef.current === controller) {
        activeRequestControllerRef.current = null;
      }
      setIsSending(false);
    }
  };

  const handleToggleFullscreen = async () => {
    if (document.fullscreenElement) {
      await document.exitFullscreen();
      return;
    }

    await document.documentElement.requestFullscreen();
  };

  const handleOpenSettings = () => {
    setDraftSettings(settings);
    setSettingsOpen(true);
  };

  const handleSaveSettings = () => {
    const normalized: AppSettings = {
      apiUrl: draftSettings.apiUrl.trim() || DEFAULT_SETTINGS.apiUrl,
      apiKey: draftSettings.apiKey.trim(),
      model: draftSettings.model.trim() || DEFAULT_SETTINGS.model,
      temperature: Number.isFinite(draftSettings.temperature)
        ? draftSettings.temperature
        : DEFAULT_SETTINGS.temperature,
      maxTokens: Number.isFinite(draftSettings.maxTokens)
        ? draftSettings.maxTokens
        : DEFAULT_SETTINGS.maxTokens,
    };

    setSettings(normalized);
    if (!saveSettings(normalized)) {
      showNotification("设置保存失败");
      return;
    }
    setSettingsOpen(false);
    showNotification("设置已保存");
  };

  const handleResetSettings = () => {
    setDraftSettings(DEFAULT_SETTINGS);
    setSettings(DEFAULT_SETTINGS);
    clearSettingsStorage();
    showNotification("已恢复默认设置");
  };

  return (
    <div className="flex h-screen flex-col bg-slate-100 text-slate-900 dark:bg-[#101012] dark:text-slate-100">
      <AppHeader
        isFullscreen={isFullscreen}
        isDarkMode={isDarkMode}
        onToggleTheme={() => setIsDarkMode((prev) => !prev)}
        onToggleFullscreen={handleToggleFullscreen}
      />

      <div className="flex min-h-0 flex-1">
        <Sidebar
          viewMode={viewMode}
          history={history}
          currentHistoryId={currentHistoryId}
          onShowHome={handleShowHome}
          onNewConversation={() => openNewConversation()}
          onOpenSettings={handleOpenSettings}
          onOpenHelp={() => setHelpOpen(true)}
          onClearHistory={handleClearHistory}
          onLoadConversation={handleLoadConversation}
          onDeleteHistory={handleDeleteHistory}
        />

        <div className="flex min-h-0 flex-1 flex-col overflow-hidden">
          <div className="flex min-h-0 flex-1">
            {viewMode === "vllm" ? (
              <VllmMetricsDashboard apiUrl={settings.apiUrl} apiKey={settings.apiKey} />
            ) : isEmptyConversation ? (
              <EmptyConversation
                draft={draft}
                attachments={attachments}
                pendingDocuments={pendingDocumentFiles}
                documentParseMode={documentParseMode}
                isSending={isSending}
                isReadingAttachments={isReadingAttachments}
                disabled={!canSend}
                onDraftChange={setDraft}
                onDocumentParseModeChange={handleDocumentParseModeChange}
                onSend={handleSend}
                onStop={handleStop}
                onSelectFiles={handleSelectFiles}
                onRemoveFile={(id) => {
                  setAttachments((prev) => prev.filter((file) => file.id !== id));
                }}
                onRemovePendingDocument={(id) => {
                  setPendingDocumentFiles((prev) => prev.filter((item) => item.id !== id));
                }}
                deepThinkEnabled={deepThinkEnabled}
                onDeepThinkChange={setDeepThinkEnabled}
              />
            ) : (
              <ChatPane
                viewMode={viewMode}
                messages={messages}
                isSending={isSending}
                onCopyNotice={showNotification}
                onUseExample={handleUseExample}
                onStartConversation={() => openNewConversation()}
                scrollRef={viewportRef}
              />
            )}
          </div>

          {viewMode === "chat" && !isEmptyConversation ? (
            <div className="shrink-0 border-t border-slate-200 bg-white/90 px-5 py-4 dark:border-zinc-800 dark:bg-[#101012]">
              <Composer
                variant="hero"
                draft={draft}
                attachments={attachments}
                pendingDocuments={pendingDocumentFiles}
                documentParseMode={documentParseMode}
                isSending={isSending}
                isReadingAttachments={isReadingAttachments}
                disabled={!canSend}
                onDraftChange={setDraft}
                onDocumentParseModeChange={handleDocumentParseModeChange}
                onSend={handleSend}
                onStop={handleStop}
                onSelectFiles={handleSelectFiles}
                onRemoveFile={(id) => {
                  setAttachments((prev) => prev.filter((file) => file.id !== id));
                }}
                onRemovePendingDocument={(id) => {
                  setPendingDocumentFiles((prev) => prev.filter((item) => item.id !== id));
                }}
                deepThinkEnabled={deepThinkEnabled}
                onDeepThinkChange={setDeepThinkEnabled}
              />
            </div>
          ) : null}

          {viewMode === "chat" ? (
            <div className="shrink-0 flex items-center justify-between border-t border-slate-200 bg-white px-5 py-3 text-xs text-slate-500 dark:border-zinc-800 dark:bg-[#18181b] dark:text-zinc-400">
              <div className="flex items-center gap-2">
                <PlugZap size={14} />
                模型: {settings.apiKey.trim() || !isAgentHostedPage() ? settings.model : "Athlon Agent"}
              </div>
              <div className="flex items-center gap-2">
                <CheckCircle2 size={14} className="text-emerald-500" />
                {settings.apiKey.trim()
                  ? "API 已配置"
                  : isAgentHostedPage()
                    ? "使用 Athlon Agent 模型"
                    : "未配置 API Key"}
              </div>
            </div>
          ) : null}
        </div>
      </div>

      <Modal
        open={helpOpen}
        title="帮助文档"
        onClose={() => setHelpOpen(false)}
        footer={
          <button
            type="button"
            className="rounded-xl bg-slate-900 px-4 py-2 text-sm font-medium text-white transition hover:bg-slate-700 dark:bg-zinc-700 dark:hover:bg-zinc-600"
            onClick={() => setHelpOpen(false)}
          >
            关闭
          </button>
        }
      >
        <h3 className="mb-3 text-base font-semibold text-slate-900 dark:text-zinc-100">基本使用说明</h3>
        <p><strong>1. 开始对话</strong></p>
        <p>点击“新对话”后，在居中输入框中输入第一条消息；发送后输入框会固定到底部。按 Enter 发送，Shift + Enter 换行。</p>
        <p><strong>2. 文件上传</strong></p>
        <p>支持拖拽或点击选择代码、文本与 YAML（.yml / .yaml）、图片、PDF、Word、Excel/CSV 和 PPTX 文件。上传文档后会出现“纯文本”“智能补图”“全部页图”三档策略（图片等非文档附件仍会立即解析预览）；点击发送时才会按当前所选模式解析文档并一并发出。纯文本只提正文，智能补图会补充关键图片，全部页图更适合扫描件或图片主导文档；老式 PPT 文件会提示先转换为 PPTX。</p>
        <p><strong>3. DeepThink</strong></p>
        <p>输入区可开启 DeepThink：请求会携带 enable_thinking（关闭时为 false，开启时为 true）。开启且模型支持时，助手气泡上方会出现可折叠的「思维链」区域展示流式 reasoning；回复结束后默认收起，可点击再次展开。</p>
        <p><strong>4. 历史记录</strong></p>
        <p>每段包含用户消息的会话都会自动保存到浏览器本地，你可以在左侧继续查看、加载或删除。</p>
        <p><strong>5. 代码复制</strong></p>
        <p>AI 回复中的代码块支持一键复制，复制成功后会显示短暂提示。</p>
        <p><strong>6. 预设示例</strong></p>
        <p>首页保留了几个常见提问示例，方便快速开始。</p>
        <p><strong>7. 监控指标</strong></p>
        <p>
          侧栏「监控指标」可打开监控页：指标地址与「设置」中的 API 地址同主机与端口，自动请求{" "}
          <code className="rounded bg-slate-100 px-1 dark:bg-zinc-800">/metrics</code>（例如 API 为
          http://host:8088/v1/chat/completions 时，指标为 http://host:8088/metrics）。浏览器直连常遇
          CORS；本地开发可在 report/html/.env.local 中设置
          VITE_VLLM_METRICS_PROXY_TARGET 为后端服务或网关基址（不含 /metrics），并将浏览器
          localStorage 键 <code className="rounded bg-slate-100 px-1 dark:bg-zinc-800">athlonVllmUseDevProxy</code>{" "}
          设为 <code className="rounded bg-slate-100 px-1 dark:bg-zinc-800">1</code>，由开发服同源转发
          /__vllm_metrics；生产环境需由网关同源暴露或放行 Origin。
        </p>
      </Modal>

      <Modal
        open={settingsOpen}
        title="设置"
        onClose={() => setSettingsOpen(false)}
        footer={
          <>
            <button
              type="button"
              className="rounded-xl border border-slate-200 px-4 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-50 dark:border-zinc-700 dark:text-zinc-300 dark:hover:bg-zinc-800"
              onClick={handleResetSettings}
            >
              重置默认
            </button>
            <button
              type="button"
              className="rounded-xl border border-slate-200 px-4 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-50 dark:border-zinc-700 dark:text-zinc-300 dark:hover:bg-zinc-800"
              onClick={() => setSettingsOpen(false)}
            >
              取消
            </button>
            <button
              type="button"
              className="rounded-xl bg-sky-600 px-4 py-2 text-sm font-medium text-white transition hover:bg-sky-700 dark:bg-blue-600 dark:hover:bg-blue-500"
              onClick={handleSaveSettings}
            >
              保存设置
            </button>
          </>
        }
      >
        <div className="space-y-4">
          <label className="block">
            <span className="mb-2 block text-sm font-medium text-slate-700 dark:text-zinc-300">API 地址</span>
            <input
              type="text"
              className="w-full rounded-2xl border border-slate-200 px-4 py-3 outline-none transition focus:border-sky-300 focus:ring-4 focus:ring-sky-100 dark:border-zinc-700 dark:bg-zinc-900 dark:text-zinc-100 dark:focus:border-blue-400 dark:focus:ring-blue-500/20"
              value={draftSettings.apiUrl}
              onChange={(event) =>
                setDraftSettings((prev) => ({ ...prev, apiUrl: event.target.value }))
              }
            />
          </label>

          <label className="block">
            <span className="mb-2 block text-sm font-medium text-slate-700 dark:text-zinc-300">API Key</span>
            <input
              type="password"
              placeholder="留空则使用 Athlon Agent 的模型"
              className="w-full rounded-2xl border border-slate-200 px-4 py-3 outline-none transition focus:border-sky-300 focus:ring-4 focus:ring-sky-100 dark:border-zinc-700 dark:bg-zinc-900 dark:text-zinc-100 dark:placeholder:text-zinc-500 dark:focus:border-blue-400 dark:focus:ring-blue-500/20"
              value={draftSettings.apiKey}
              onChange={(event) =>
                setDraftSettings((prev) => ({ ...prev, apiKey: event.target.value }))
              }
            />
            <p className="mt-2 text-xs text-slate-500 dark:text-zinc-500">
              填写后会以 Bearer 方式发到上面的 API 地址。留空时，从 Athlon Agent 打开的页面会使用 Agent 已配置的模型，不再单独填写密钥。
            </p>
          </label>

          <label className="block">
            <span className="mb-2 block text-sm font-medium text-slate-700 dark:text-zinc-300">模型名称</span>
            <input
              type="text"
              className="w-full rounded-2xl border border-slate-200 px-4 py-3 outline-none transition focus:border-sky-300 focus:ring-4 focus:ring-sky-100 dark:border-zinc-700 dark:bg-zinc-900 dark:text-zinc-100 dark:focus:border-blue-400 dark:focus:ring-blue-500/20"
              value={draftSettings.model}
              onChange={(event) =>
                setDraftSettings((prev) => ({ ...prev, model: event.target.value }))
              }
            />
          </label>

          <div className="grid gap-4 md:grid-cols-2">
            <label className="block">
              <span className="mb-2 block text-sm font-medium text-slate-700 dark:text-zinc-300">Temperature</span>
              <input
                type="number"
                min={0}
                max={1}
                step={0.1}
                className="w-full rounded-2xl border border-slate-200 px-4 py-3 outline-none transition focus:border-sky-300 focus:ring-4 focus:ring-sky-100 dark:border-zinc-700 dark:bg-zinc-900 dark:text-zinc-100 dark:focus:border-blue-400 dark:focus:ring-blue-500/20"
                value={draftSettings.temperature}
                onChange={(event) =>
                  setDraftSettings((prev) => ({
                    ...prev,
                    temperature: Number(event.target.value),
                  }))
                }
              />
            </label>

            <label className="block">
              <span className="mb-2 block text-sm font-medium text-slate-700 dark:text-zinc-300">Max Tokens</span>
              <input
                type="number"
                className="w-full rounded-2xl border border-slate-200 px-4 py-3 outline-none transition focus:border-sky-300 focus:ring-4 focus:ring-sky-100 dark:border-zinc-700 dark:bg-zinc-900 dark:text-zinc-100 dark:focus:border-blue-400 dark:focus:ring-blue-500/20"
                value={draftSettings.maxTokens}
                onChange={(event) =>
                  setDraftSettings((prev) => ({
                    ...prev,
                    maxTokens: Number(event.target.value),
                  }))
                }
              />
            </label>
          </div>
        </div>
      </Modal>

      {notification ? (
        <div className="pointer-events-none fixed right-5 top-5 z-[60] rounded-2xl bg-slate-900 px-4 py-3 text-sm text-white shadow-xl">
          {notification}
        </div>
      ) : null}
    </div>
  );
}
