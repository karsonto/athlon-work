import { LogOut, MessageSquarePlus, PanelLeftClose, PanelLeft, Plus, X } from "lucide-react";
import type { RoomTab } from "../types";

interface RoomSidebarProps {
  rooms: RoomTab[];
  activeKey: string | null;
  collapsed: boolean;
  onToggleCollapsed: () => void;
  onSelect: (tab: RoomTab) => void;
  onCreate: () => void;
  onJoin: () => void;
  onCopyInvite: (tab: RoomTab) => void;
  onLeave: (tab: RoomTab) => void;
  onCloseHost: (tab: RoomTab) => void;
  canCreateLocal: boolean;
}

export function roomKey(tab: Pick<RoomTab, "id" | "baseUrl">): string {
  return `${tab.baseUrl}|${tab.id}`;
}

function avatarColor(seed: string): string {
  let hash = 0;
  for (let i = 0; i < seed.length; i++) {
    hash = seed.charCodeAt(i) + ((hash << 5) - hash);
  }
  const colors = [
    "#5b8ff9",
    "#61ddaa",
    "#65789b",
    "#f6bd16",
    "#7262fd",
    "#78d3f8",
    "#9661bc",
    "#f6903d",
  ];
  return colors[Math.abs(hash) % colors.length];
}

export function RoomSidebar({
  rooms,
  activeKey,
  collapsed,
  onToggleCollapsed,
  onSelect,
  onCreate,
  onJoin,
  onCopyInvite,
  onLeave,
  onCloseHost,
  canCreateLocal,
}: RoomSidebarProps) {
  return (
    <aside
      className={`flex h-full shrink-0 flex-col border-r border-white/10 bg-[#161616] transition-[width] ${
        collapsed ? "w-14" : "w-[240px]"
      }`}
    >
      <div className="flex h-12 items-center justify-between border-b border-white/10 px-2">
        {!collapsed ? (
          <span className="truncate px-2 text-sm font-medium text-[var(--text-primary)]">房间</span>
        ) : (
          <span className="sr-only">房间</span>
        )}
        <button
          type="button"
          className="rounded-lg p-2 text-[var(--text-muted)] hover:bg-white/5"
          onClick={onToggleCollapsed}
          aria-label={collapsed ? "展开侧栏" : "折叠侧栏"}
        >
          {collapsed ? <PanelLeft size={18} /> : <PanelLeftClose size={18} />}
        </button>
      </div>

      <div className="flex-1 overflow-y-auto py-2">
        {rooms.map((tab) => {
          const key = roomKey(tab);
          const active = key === activeKey;
          return (
            <div key={key} className={`group relative mx-1.5 mb-0.5 ${active ? "rounded-xl bg-white/10" : ""}`}>
              <button
                type="button"
                className={`flex w-full items-center gap-2.5 rounded-xl px-2 py-2 text-left hover:bg-white/5 ${
                  active ? "bg-transparent" : ""
                }`}
                onClick={() => onSelect(tab)}
                title={tab.title}
              >
                <div
                  className="relative flex h-9 w-9 shrink-0 items-center justify-center rounded-lg text-sm font-semibold text-white"
                  style={{ background: avatarColor(tab.id) }}
                >
                  {tab.title.trim().slice(0, 1).toUpperCase() || "R"}
                  {tab.unread > 0 ? (
                    <span className="absolute -top-1 -right-1 flex h-4 min-w-4 items-center justify-center rounded-full bg-[#f25555] px-1 text-[10px] font-medium text-white">
                      {tab.unread > 99 ? "99+" : tab.unread}
                    </span>
                  ) : null}
                </div>
                {!collapsed ? (
                  <div className="min-w-0 flex-1">
                    <div className="truncate text-sm text-[var(--text-primary)]">{tab.title}</div>
                    <div className="truncate text-[11px] text-[var(--text-muted)]">
                      {tab.role === "host" ? "我创建的" : "已加入"}
                    </div>
                  </div>
                ) : null}
              </button>
              {!collapsed ? (
                <div className="absolute top-1 right-1 hidden gap-0.5 group-hover:flex">
                  <button
                    type="button"
                    className="rounded p-1 text-[var(--text-muted)] hover:bg-white/10 hover:text-white"
                    title="复制邀请"
                    onClick={(e) => {
                      e.stopPropagation();
                      onCopyInvite(tab);
                    }}
                  >
                    <MessageSquarePlus size={14} />
                  </button>
                  {tab.role === "host" ? (
                    <button
                      type="button"
                      className="rounded p-1 text-[var(--text-muted)] hover:bg-white/10 hover:text-red-300"
                      title="关闭房间"
                      onClick={(e) => {
                        e.stopPropagation();
                        onCloseHost(tab);
                      }}
                    >
                      <X size={14} />
                    </button>
                  ) : (
                    <button
                      type="button"
                      className="rounded p-1 text-[var(--text-muted)] hover:bg-white/10 hover:text-red-300"
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

      <div className={`border-t border-white/10 p-2 ${collapsed ? "flex flex-col gap-1" : "flex gap-2"}`}>
        {canCreateLocal ? (
          <button
            type="button"
            className={`flex items-center justify-center gap-1.5 rounded-xl bg-[#12b7f5] text-sm font-medium text-white hover:brightness-110 ${
              collapsed ? "h-9 w-full" : "flex-1 py-2"
            }`}
            onClick={onCreate}
            title="新建房间"
          >
            <Plus size={16} />
            {!collapsed ? "新建" : null}
          </button>
        ) : null}
        <button
          type="button"
          className={`flex items-center justify-center gap-1.5 rounded-xl border border-white/15 text-sm text-[var(--text-primary)] hover:bg-white/5 ${
            collapsed ? "h-9 w-full" : "flex-1 py-2"
          }`}
          onClick={onJoin}
          title="加入房间"
        >
          <MessageSquarePlus size={16} />
          {!collapsed ? "加入" : null}
        </button>
      </div>
    </aside>
  );
}
