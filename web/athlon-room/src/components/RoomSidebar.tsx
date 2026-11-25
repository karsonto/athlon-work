import { useEffect, useState, type ReactNode } from "react";
import {
  Bot,
  Copy,
  LogOut,
  MessageSquarePlus,
  Pencil,
  Plus,
  X,
} from "lucide-react";
import type { RoomTab } from "../types";
import { agentRuntimeModeLabel } from "../lib/runtimeMode";
import { AthlonIcon } from "./AthlonIcon";

interface RoomSidebarProps {
  rooms: RoomTab[];
  activeKey: string | null;
  onSelect: (tab: RoomTab) => void;
  onCreate: () => void;
  onJoin: () => void;
  onCopyInvite: (tab: RoomTab) => void;
  onLeave: (tab: RoomTab) => void;
  onCloseHost: (tab: RoomTab) => void;
  onOpenSettings: (tab: RoomTab) => void;
  onOpenAgentCaps: (tab: RoomTab) => void;
  canCreateLocal: boolean;
}

export function roomKey(tab: Pick<RoomTab, "id" | "baseUrl">): string {
  return `${tab.baseUrl}|${tab.id}`;
}

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

function MenuButton({
  collapsed,
  icon,
  label,
  onClick,
  primary,
}: {
  collapsed: boolean;
  icon: ReactNode;
  label: string;
  onClick: () => void;
  primary?: boolean;
}) {
  return (
    <button
      type="button"
      title={label}
      className={[
        "flex items-center rounded-2xl text-left text-sm transition",
        collapsed ? "mx-auto h-9 w-9 shrink-0 justify-center" : "w-full gap-3 px-4 py-3",
        primary
          ? "bg-sky-600 text-white shadow-sm hover:bg-sky-700"
          : "text-slate-600 hover:bg-slate-50 hover:text-slate-900 dark:text-zinc-400 dark:hover:bg-zinc-800 dark:hover:text-zinc-100",
      ].join(" ")}
      onClick={onClick}
    >
      <span className="inline-flex h-[18px] w-[18px] shrink-0 items-center justify-center">{icon}</span>
      {!collapsed ? <span className="min-w-0 truncate">{label}</span> : null}
    </button>
  );
}

function avatarColor(seed: string): string {
  let hash = 0;
  for (let i = 0; i < seed.length; i++) {
    hash = seed.charCodeAt(i) + ((hash << 5) - hash);
  }
  const colors = ["#0ea5e9", "#0284c7", "#6366f1", "#14b8a6", "#f59e0b", "#8b5cf6", "#64748b", "#f97316"];
  return colors[Math.abs(hash) % colors.length];
}

export function RoomSidebar({
  rooms,
  activeKey,
  onSelect,
  onCreate,
  onJoin,
  onCopyInvite,
  onLeave,
  onCloseHost,
  onOpenSettings,
  onOpenAgentCaps,
  canCreateLocal,
}: RoomSidebarProps) {
  const isDesktop = useWideSidebarBreakpoint();
  const [railHover, setRailHover] = useState(false);
  const expanded = !isDesktop || railHover;
  const collapsed = !expanded;

  return (
    <div
      className={["relative shrink-0 self-stretch", isDesktop ? "w-14" : "w-80"].join(" ")}
      onMouseEnter={() => setRailHover(true)}
      onMouseLeave={() => setRailHover(false)}
    >
      <aside
        className={[
          "flex h-full flex-col overflow-hidden border-r border-sky-100 bg-white/95 transition-[width,box-shadow,padding] duration-200 ease-out dark:border-zinc-800 dark:bg-[#18181b]",
          collapsed ? "w-14 px-2 py-3" : "w-80 p-4",
          isDesktop && expanded
            ? "absolute inset-y-0 left-0 z-20 w-80 p-4 shadow-xl shadow-sky-100/60 dark:shadow-black/40"
            : "relative w-full",
        ].join(" ")}
      >
        <div className={collapsed ? "mb-3 flex justify-center" : "mb-3 px-1"}>
          {!collapsed ? (
            <div className="flex items-center gap-3">
              <AthlonIcon className="h-9 w-9 shrink-0 rounded-2xl shadow-sm" />
              <div className="min-w-0">
                <div className="text-sm font-semibold text-slate-900 dark:text-zinc-100">Athlon Teams</div>
                <div className="text-xs text-slate-500 dark:text-zinc-400">局域网多人协作</div>
              </div>
            </div>
          ) : (
            <AthlonIcon className="h-9 w-9 shrink-0 rounded-2xl shadow-sm" />
          )}
        </div>

        <div className={["mb-3", collapsed ? "space-y-2" : "space-y-1"].join(" ")}>
          {canCreateLocal ? (
            <MenuButton
              collapsed={collapsed}
              icon={<Plus size={18} />}
              label="新建房间"
              primary
              onClick={onCreate}
            />
          ) : null}
          <MenuButton
            collapsed={collapsed}
            icon={<MessageSquarePlus size={18} />}
            label="加入房间"
            onClick={onJoin}
          />
        </div>

        <div className="min-h-0 flex-1 space-y-1 overflow-y-auto">
          {!collapsed ? (
            <div className="mb-1 px-3 text-[11px] font-medium tracking-wide text-slate-400 uppercase dark:text-zinc-500">
              房间列表
            </div>
          ) : null}
          {rooms.map((tab) => {
            const key = roomKey(tab);
            const active = key === activeKey;
            return (
              <div
                key={key}
                className={[
                  "group relative rounded-2xl transition",
                  active
                    ? "bg-sky-50 text-sky-700 shadow-sm ring-1 ring-sky-100 dark:bg-blue-500/15 dark:text-blue-300 dark:ring-blue-400/20"
                    : "text-slate-700 hover:bg-slate-50 dark:text-zinc-300 dark:hover:bg-zinc-800",
                ].join(" ")}
              >
                <button
                  type="button"
                  className={[
                    "flex w-full items-center rounded-2xl text-left",
                    collapsed ? "justify-center px-0 py-1.5" : "gap-3 px-3 py-2.5",
                  ].join(" ")}
                  onClick={() => onSelect(tab)}
                  title={tab.title}
                >
                  <div
                    className="relative flex h-9 w-9 shrink-0 items-center justify-center rounded-xl text-sm font-semibold text-white"
                    style={{ background: avatarColor(tab.id) }}
                  >
                    {tab.title.trim().slice(0, 1).toUpperCase() || "R"}
                    {tab.unread > 0 ? (
                      <span className="absolute -top-1 -right-1 flex h-4 min-w-4 items-center justify-center rounded-full bg-rose-500 px-1 text-[10px] font-medium text-white">
                        {tab.unread > 99 ? "99+" : tab.unread}
                      </span>
                    ) : null}
                  </div>
                  {!collapsed ? (
                    <div className="min-w-0 flex-1">
                      <div className="truncate text-sm font-medium">{tab.title}</div>
                      <div className="truncate text-[11px] opacity-70">
                        {tab.role === "host" ? "我创建的" : "已加入"}
                        {` · ${agentRuntimeModeLabel(tab.agentRuntimeMode)}`}
                        {tab.workspaceName || tab.workspaceRoot
                          ? ` · ${tab.workspaceName || tab.workspaceRoot}`
                          : ""}
                      </div>
                    </div>
                  ) : null}
                </button>

                {!collapsed ? (
                  <div className="absolute top-1.5 right-1.5 hidden gap-0.5 group-hover:flex">
                    <button
                      type="button"
                      className="rounded-lg p-1.5 text-slate-400 hover:bg-white hover:text-sky-700 dark:hover:bg-zinc-700 dark:hover:text-sky-300"
                      title="复制邀请"
                      onClick={(e) => {
                        e.stopPropagation();
                        onCopyInvite(tab);
                      }}
                    >
                      <Copy size={14} />
                    </button>
                    {tab.role === "host" ? (
                      <>
                        <button
                          type="button"
                          className="rounded-lg p-1.5 text-slate-400 hover:bg-white hover:text-sky-700 dark:hover:bg-zinc-700 dark:hover:text-sky-300"
                          title="Agent 能力"
                          onClick={(e) => {
                            e.stopPropagation();
                            onOpenAgentCaps(tab);
                          }}
                        >
                          <Bot size={14} />
                        </button>
                        <button
                          type="button"
                          className="rounded-lg p-1.5 text-slate-400 hover:bg-white hover:text-sky-700 dark:hover:bg-zinc-700 dark:hover:text-sky-300"
                          title="房间设置"
                          onClick={(e) => {
                            e.stopPropagation();
                            onOpenSettings(tab);
                          }}
                        >
                          <Pencil size={14} />
                        </button>
                        <button
                          type="button"
                          className="rounded-lg p-1.5 text-slate-400 hover:bg-white hover:text-rose-600 dark:hover:bg-zinc-700 dark:hover:text-rose-300"
                          title="关闭房间"
                          onClick={(e) => {
                            e.stopPropagation();
                            onCloseHost(tab);
                          }}
                        >
                          <X size={14} />
                        </button>
                      </>
                    ) : (
                      <button
                        type="button"
                        className="rounded-lg p-1.5 text-slate-400 hover:bg-white hover:text-rose-600 dark:hover:bg-zinc-700 dark:hover:text-rose-300"
                        title="退出"
                        onClick={(e) => {
                          e.stopPropagation();
                          onLeave(tab);
                        }}
                      >
                        <LogOut size={14} />
                      </button>
                    )}
                  </div>
                ) : null}
              </div>
            );
          })}
        </div>
      </aside>
    </div>
  );
}
