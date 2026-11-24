import type { DocumentParseMode, FileAttachment } from "../types";
import { Composer, type PendingDocumentItem } from "./Composer";
import { WelcomeBuddyIcon } from "./WelcomeBuddyIcon";

interface EmptyConversationProps {
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

export function EmptyConversation({
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
}: EmptyConversationProps) {
  return (
    <div className="flex h-full w-full flex-1 items-center justify-center overflow-y-auto bg-[radial-gradient(circle_at_top,_rgba(56,189,248,0.14),transparent_34%),linear-gradient(to_bottom,_#f8fbff,_#eef6ff)] px-6 py-10 dark:bg-[radial-gradient(circle_at_top,_rgba(59,130,246,0.16),transparent_34%),linear-gradient(to_bottom,_#111113,_#101012)]">
      <div className="w-full max-w-5xl">
        <div className="mx-auto mb-12 max-w-4xl">
          <div className="mb-6 flex items-center justify-center gap-3.5">
            <WelcomeBuddyIcon className="h-11 w-11 shrink-0" />
            <div className="min-w-0 text-left">
              <h2 className="text-[22px] font-semibold leading-tight tracking-tight text-slate-950 dark:text-zinc-100">
                与Athlon 共事
              </h2>
              <p className="mt-1 text-[13px] leading-5 text-slate-500 dark:text-zinc-400">
                懂代码，懂业务，更懂金融
              </p>
            </div>
          </div>
        </div>

        <Composer
          variant="hero"
          draft={draft}
          attachments={attachments}
          pendingDocuments={pendingDocuments}
          documentParseMode={documentParseMode}
          isSending={isSending}
          isReadingAttachments={isReadingAttachments}
          disabled={disabled}
          onDraftChange={onDraftChange}
          onDocumentParseModeChange={onDocumentParseModeChange}
          onSend={onSend}
          onStop={onStop}
          onSelectFiles={onSelectFiles}
          onRemoveFile={onRemoveFile}
          onRemovePendingDocument={onRemovePendingDocument}
          deepThinkEnabled={deepThinkEnabled}
          onDeepThinkChange={onDeepThinkChange}
        />
      </div>
    </div>
  );
}
