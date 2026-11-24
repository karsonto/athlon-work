import type { ReactNode } from "react";
import { useEffect, useState } from "react";
import {
  Cog,
  HelpCircle,
  History,
  Home,
  MessageSquarePlus,
  Trash2,
} from "lucide-react";
import type { ConversationHistoryItem, ViewMode } from "../types";

function useWideSidebarBreakpoint() {
  const [wide, setWide] = useState(() =>
    typeof window !== "undefined" ? window.matchMedia("(min-width: 641px)").matches : false,
  );
  useEffect(() => {
    const mq = window.matchMedia("(min-width: 641px)");
    const apply = () => setWide(mq.matches);
    apply();
    mq.addEventListener("change", apply);
    return () => mq.removeEventListener("change", apply);
  }, []);
  return wide;
}

interface SidebarProps {
  viewMode: ViewMode;
  history: ConversationHistoryItem[];
  currentHistoryId: string | null;
  onShowHome: () => void;
  onNewConversation: () => void;
  onOpenSettings: () => void;
  onOpenHelp: () => void;
  onClearHistory: () => void;
  onLoadConversation: (id: string) => void;
  onDeleteHistory: (id: string) => void;
}

function MenuButton({
  active,
  collapsed,
  icon,
  label,
  onClick,
}: {
  active?: boolean;
  collapsed: boolean;
  icon: ReactNode;
  label: string;
  onClick: () => void;
}) {
  return (
    <button
      type="button"
      title={label}
      className={[
        "flex w-full items-center rounded-2xl py-3 text-left text-sm transition",
        collapsed ? "justify-center gap-0 px-0" : "gap-3 px-4",
        active
          ? "bg-sky-50 text-sky-700 shadow-sm ring-1 ring-sky-100 dark:bg-blue-500/15 dark:text-blue-300 dark:ring-blue-400/20"
          : "text-slate-600 hover:bg-slate-50 hover:text-slate-900 dark:text-zinc-400 dark:hover:bg-zinc-800 dark:hover:text-zinc-100",
      ].join(" ")}
      onClick={onClick}
    >
      {icon}
      {!collapsed ? <span className="min-w-0 truncate">{label}</span> : null}
    </button>
  );
}

export function Sidebar({
  viewMode,
  history,
  currentHistoryId,
  onShowHome,
  onNewConversation,
  onOpenSettings,
  onOpenHelp,
  onClearHistory,
  onLoadConversation,
  onDeleteHistory,
}: SidebarProps) {
  const isDesktop = useWideSidebarBreakpoint();
  const [railHover, setRailHover] = useState(false);
  const expanded = !isDesktop || railHover;
  const collapsed = !expanded;

  return (
    <div
      className={[
        "relative shrink-0 self-stretch",
        isDesktop ? "w-14" : "w-80",
      ].join(" ")}
      onMouseEnter={() => setRailHover(true)}
      onMouseLeave={() => setRailHover(false)}
    >
      <aside
        className={[
          "flex h-full flex-col overflow-hidden border-r border-sky-100 bg-white/95 p-4 transition-[width,box-shadow] duration-200 ease-out dark:border-zinc-800 dark:bg-[#18181b]",
          isDesktop ? "absolute left-0 top-0 z-30" : "relative",
          expanded ? "w-80 shadow-xl ring-1 ring-slate-200/80 dark:shadow-2xl dark:ring-zinc-700/50" : "w-14 shadow-none ring-0",
        ].join(" ")}
      >
        <div className={["mb-5", collapsed ? "hidden" : "block"].join(" ")}>
          <p className="text-xs font-medium uppercase tracking-[0.2em] text-slate-400 dark:text-zinc-500">对话</p>
        </div>

        <div className="space-y-2">
          <MenuButton
            collapsed={collapsed}
            active={viewMode === "home"}
            icon={<Home size={18} />}
            label="首页"
            onClick={onShowHome}
          />
          <MenuButton
            collapsed={collapsed}
            active={viewMode === "chat" && currentHistoryId === null}
            icon={<MessageSquarePlus size={18} />}
            label="新对话"
            onClick={onNewConversation}
          />
          <MenuButton collapsed={collapsed} icon={<Cog size={18} />} label="设置" onClick={onOpenSettings} />
          <MenuButton collapsed={collapsed} icon={<HelpCircle size={18} />} label="帮助" onClick={onOpenHelp} />
        </div>

        <div className={["mt-8 flex items-center justify-between", collapsed ? "hidden" : "flex"].join(" ")}>
          <div className="flex min-w-0 items-center gap-2 text-sm font-medium text-slate-700 dark:text-zinc-300">
            <History size={16} className="shrink-0" />
            <span className="truncate">历史记录</span>
          </div>
          <button
            type="button"
            className="inline-flex shrink-0 items-center gap-1 rounded-lg px-2 py-1 text-xs text-slate-500 transition hover:bg-slate-100 hover:text-slate-800 dark:text-zinc-500 dark:hover:bg-zinc-800 dark:hover:text-zinc-200"
            onClick={onClearHistory}
          >
            <Trash2 size={14} />
            清空
          </button>
        </div>

        {collapsed ? (
          <div
            className="mt-auto flex justify-center pb-2 pt-6"
            title="悬停侧边栏展开，查看历史记录"
            aria-hidden
          >
            <History size={18} className="text-slate-400 dark:text-zinc-500" />
          </div>
        ) : (
          <div className="mt-3 flex min-h-0 flex-1 flex-col overflow-hidden">
            <div className="min-h-0 flex-1 space-y-2 overflow-y-auto pr-1">
              {history.length === 0 ? (
                <div className="rounded-2xl border border-dashed border-slate-200 px-4 py-5 text-sm text-slate-500 dark:border-zinc-700 dark:text-zinc-500">
                  暂无历史记录，开始一段新对话后会自动保存到本地。
                </div>
              ) : (
                history.map((item) => (
                  <div
                    key={item.id}
                    className={[
                      "group rounded-2xl border p-3 transition",
                      item.id === currentHistoryId
                        ? "border-sky-200 bg-sky-50 dark:border-blue-400/30 dark:bg-blue-500/10"
                        : "border-slate-200 bg-white hover:border-slate-300 hover:bg-slate-50 dark:border-zinc-800 dark:bg-zinc-900/40 dark:hover:border-zinc-700 dark:hover:bg-zinc-800/70",
                    ].join(" ")}
                  >
                    <button
                      type="button"
                      className="w-full text-left"
                      onClick={() => onLoadConversation(item.id)}
                    >
                      <div className="truncate text-sm font-medium text-slate-900 dark:text-zinc-100">{item.title}</div>
                      <div className="mt-1 text-xs text-slate-500 dark:text-zinc-500">
                        {new Date(item.timestamp).toLocaleString("zh-CN")}
                      </div>
                    </button>
                    <div className="mt-3 flex justify-end">
                      <button
                        type="button"
                        className="inline-flex items-center gap-1 rounded-lg px-2 py-1 text-xs text-slate-500 transition hover:bg-white hover:text-rose-600 dark:text-zinc-500 dark:hover:bg-zinc-900 dark:hover:text-rose-400"
                        onClick={() => onDeleteHistory(item.id)}
                      >
                        <Trash2 size={14} />
                        删除
                      </button>
                    </div>
                  </div>
                ))
              )}
            </div>
          </div>
        )}
      </aside>
    </div>
  );
}
