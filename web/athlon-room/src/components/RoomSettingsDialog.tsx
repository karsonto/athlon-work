import { useEffect, useState } from "react";
import type { RoomTab, WorkspaceItem } from "../types";
import { Modal } from "./Modal";

interface RoomSettingsDialogProps {
  open: boolean;
  tab: RoomTab | null;
  workspaces: WorkspaceItem[];
  saving: boolean;
  error: string | null;
  onClose: () => void;
  onSave: (input: {
    title: string;
    workspaceId: string | null;
    workspaceRoot: string | null;
  }) => void;
}

export function RoomSettingsDialog({
  open,
  tab,
  workspaces,
  saving,
  error,
  onClose,
  onSave,
}: RoomSettingsDialogProps) {
  const [title, setTitle] = useState("");
  const [workspaceKey, setWorkspaceKey] = useState("");

  useEffect(() => {
    if (!open || !tab) return;
    setTitle(tab.title);
    if (tab.workspaceId) {
      setWorkspaceKey(`id:${tab.workspaceId}`);
    } else if (tab.workspaceRoot) {
      setWorkspaceKey(`root:${tab.workspaceRoot}`);
    } else {
      setWorkspaceKey("");
    }
  }, [open, tab]);

  if (!tab) return null;

  return (
    <Modal
      open={open}
      title="房间设置"
      onClose={onClose}
      footer={
        <>
          <button
            type="button"
            className="rounded-xl px-4 py-2 text-sm text-slate-600 transition hover:bg-slate-100 dark:text-zinc-300 dark:hover:bg-zinc-800"
            onClick={onClose}
            disabled={saving}
          >
            取消
          </button>
          <button
            type="button"
            className="rounded-xl bg-sky-600 px-4 py-2 text-sm font-medium text-white transition hover:bg-sky-700 disabled:opacity-50"
            disabled={saving || !title.trim()}
            onClick={() => {
              let workspaceId: string | null = null;
              let workspaceRoot: string | null = null;
              if (workspaceKey.startsWith("id:")) {
                workspaceId = workspaceKey.slice(3);
                workspaceRoot = workspaces.find((w) => w.id === workspaceId)?.rootPath ?? null;
              } else if (workspaceKey.startsWith("root:")) {
                workspaceRoot = workspaceKey.slice(5);
              }
              onSave({ title: title.trim(), workspaceId, workspaceRoot });
            }}
          >
            {saving ? "保存中…" : "保存"}
          </button>
        </>
      }
    >
      <label className="mb-1 block text-sm font-medium text-slate-700 dark:text-zinc-200">房间名称</label>
      <input
        className="mb-4 w-full rounded-2xl border border-slate-200 bg-slate-50 px-3 py-2.5 text-sm text-slate-900 outline-none focus:border-sky-300 focus:ring-2 focus:ring-sky-100 dark:border-zinc-700 dark:bg-zinc-800 dark:text-zinc-100"
        value={title}
        maxLength={64}
        onChange={(e) => setTitle(e.target.value)}
      />

      <label className="mb-1 block text-sm font-medium text-slate-700 dark:text-zinc-200">工作区</label>
      <p className="mb-2 text-xs text-slate-500 dark:text-zinc-400">
        @agent 将在此工作区中读写文件。未选择时使用 Athlon 当前活动工作区。
      </p>
      <select
        className="w-full rounded-2xl border border-slate-200 bg-slate-50 px-3 py-2.5 text-sm text-slate-900 outline-none focus:border-sky-300 focus:ring-2 focus:ring-sky-100 dark:border-zinc-700 dark:bg-zinc-800 dark:text-zinc-100"
        value={workspaceKey}
        onChange={(e) => setWorkspaceKey(e.target.value)}
      >
        <option value="">使用当前活动工作区</option>
        {workspaces.map((w) => (
          <option key={w.id || w.rootPath} value={w.id ? `id:${w.id}` : `root:${w.rootPath}`}>
            {w.name}
            {w.rootPath ? ` — ${w.rootPath}` : ""}
          </option>
        ))}
      </select>
      {error ? <p className="mt-3 text-sm text-red-500">{error}</p> : null}
    </Modal>
  );
}
