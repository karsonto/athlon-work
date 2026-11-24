import { useEffect, useMemo, useState } from "react";
import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";
import hljs from "highlight.js/lib/core";
import javascript from "highlight.js/lib/languages/javascript";
import typescript from "highlight.js/lib/languages/typescript";
import python from "highlight.js/lib/languages/python";
import java from "highlight.js/lib/languages/java";
import cpp from "highlight.js/lib/languages/cpp";
import json from "highlight.js/lib/languages/json";
import xml from "highlight.js/lib/languages/xml";
import markdown from "highlight.js/lib/languages/markdown";
import type { Components } from "react-markdown";
import { Check, Copy, Eye } from "lucide-react";

hljs.registerLanguage("javascript", javascript);
hljs.registerLanguage("js", javascript);
hljs.registerLanguage("typescript", typescript);
hljs.registerLanguage("ts", typescript);
hljs.registerLanguage("python", python);
hljs.registerLanguage("py", python);
hljs.registerLanguage("java", java);
hljs.registerLanguage("cpp", cpp);
hljs.registerLanguage("c", cpp);
hljs.registerLanguage("json", json);
hljs.registerLanguage("html", xml);
hljs.registerLanguage("xml", xml);
hljs.registerLanguage("markdown", markdown);
hljs.registerLanguage("md", markdown);

interface MarkdownContentProps {
  content: string;
  onCopy?: (message: string) => void;
  onPreviewHtml?: (code: string) => void;
}

async function copyTextToClipboard(text: string) {
  if (navigator.clipboard?.writeText && window.isSecureContext) {
    await navigator.clipboard.writeText(text);
    return;
  }

  const textarea = document.createElement("textarea");
  textarea.value = text;
  textarea.setAttribute("readonly", "");
  textarea.style.position = "fixed";
  textarea.style.top = "0";
  textarea.style.left = "0";
  textarea.style.opacity = "0";
  document.body.appendChild(textarea);
  textarea.focus();
  textarea.select();

  const success = document.execCommand("copy");
  document.body.removeChild(textarea);

  if (!success) {
    throw new Error("Clipboard copy command failed");
  }
}

function extractLanguage(className?: string) {
  return className?.replace("language-", "").trim() || "";
}

function InlineCode({ children }: { children?: React.ReactNode }) {
  return (
    <code className="rounded bg-slate-200 px-1.5 py-0.5 font-mono text-[0.9em] text-slate-800 dark:bg-zinc-800 dark:text-zinc-100">
      {children}
    </code>
  );
}

export function MarkdownContent({ content, onCopy, onPreviewHtml }: MarkdownContentProps) {
  const [copiedBlockId, setCopiedBlockId] = useState<string | null>(null);

  useEffect(() => {
    if (!copiedBlockId) {
      return;
    }

    const timeoutId = window.setTimeout(() => {
      setCopiedBlockId(null);
    }, 1600);

    return () => {
      window.clearTimeout(timeoutId);
    };
  }, [copiedBlockId]);

  const components = useMemo<Components>(
    () => ({
      code({ className, children, node, ...props }) {
        const rawCode = String(children ?? "").replace(/\n$/, "");
        const language = extractLanguage(className);
        const canPreviewHtml = language === "html";
        const blockId = node?.position?.start.offset !== undefined
          ? `${node.position.start.offset}-${node.position.end?.offset ?? node.position.start.offset}`
          : null;
        const isCopied = blockId !== null && copiedBlockId === blockId;

        if (!className) {
          return <InlineCode>{children}</InlineCode>;
        }

        const highlighted = language && hljs.getLanguage(language)
          ? hljs.highlight(rawCode, { language }).value
          : hljs.highlightAuto(rawCode).value;

        return (
          <div className="group relative my-4 overflow-hidden rounded-2xl border border-slate-800 bg-slate-950">
            <div className="flex items-center justify-between border-b border-slate-800 px-4 py-2 text-xs text-slate-300">
              <span>{language || "代码"}</span>
              <div className="flex items-center gap-2">
                {canPreviewHtml ? (
                  <button
                    type="button"
                    className="inline-flex items-center gap-1 rounded-md border border-slate-700 px-2 py-1 text-slate-300 transition hover:border-slate-500 hover:text-white"
                    onClick={() => onPreviewHtml?.(rawCode)}
                  >
                    <Eye size={14} />
                    预览
                  </button>
                ) : null}
                <button
                  type="button"
                  className={[
                    "inline-flex items-center gap-1 rounded-md border px-2 py-1 transition",
                    isCopied
                      ? "border-emerald-500/60 bg-emerald-500/10 text-emerald-300"
                      : "border-slate-700 text-slate-300 hover:border-slate-500 hover:text-white",
                  ].join(" ")}
                  onClick={() => {
                    copyTextToClipboard(rawCode).then(() => {
                      setCopiedBlockId(blockId);
                      onCopy?.("代码已复制到剪贴板");
                    }).catch((error) => {
                      console.error("复制失败:", error);
                      onCopy?.("复制失败，请手动选择代码");
                    });
                  }}
                >
                  {isCopied ? <Check size={14} /> : <Copy size={14} />}
                  {isCopied ? "已复制" : "复制"}
                </button>
              </div>
            </div>
            <pre className="overflow-x-auto p-4 text-sm leading-6 text-slate-100">
              <code
                {...props}
                className={className}
                dangerouslySetInnerHTML={{ __html: highlighted }}
              />
            </pre>
          </div>
        );
      },
      p({ children }) {
        return <p className="mb-3 last:mb-0">{children}</p>;
      },
      ul({ children }) {
        return <ul className="mb-3 list-disc space-y-2 pl-6">{children}</ul>;
      },
      ol({ children }) {
        return <ol className="mb-3 list-decimal space-y-2 pl-6">{children}</ol>;
      },
      table({ children }) {
        return (
          <div className="my-4 overflow-x-auto rounded-xl border border-slate-200 dark:border-zinc-700">
            <table className="min-w-full border-collapse text-sm">{children}</table>
          </div>
        );
      },
      th({ children }) {
        return <th className="bg-slate-100 px-3 py-2 text-left font-semibold dark:bg-zinc-800">{children}</th>;
      },
      td({ children }) {
        return <td className="border-t border-slate-200 px-3 py-2 align-top dark:border-zinc-700">{children}</td>;
      },
      a({ href, children }) {
        return (
          <a
            href={href}
            target="_blank"
            rel="noreferrer"
            className="text-sky-600 underline underline-offset-2 dark:text-blue-300"
          >
            {children}
          </a>
        );
      },
    }),
    [copiedBlockId, onCopy, onPreviewHtml],
  );

  return (
    <div className="prose prose-slate max-w-none prose-pre:m-0 prose-code:before:hidden prose-code:after:hidden dark:prose-invert">
      <ReactMarkdown remarkPlugins={[remarkGfm]} components={components}>
        {content}
      </ReactMarkdown>
    </div>
  );
}
