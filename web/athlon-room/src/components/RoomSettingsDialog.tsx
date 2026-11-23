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
  onOpenAgentCaps?: () => void;
}

export function RoomSettingsDialog({
  open,
  tab,
  workspaces,
  saving,
  error,
  onClose,
  onSave,
  onOpenAgentCaps,
}: RoomSettingsDialogProps) {
  const [title, setTitle] = useState("");
  const [presetKey, setPresetKey] = useState("");
  const [customPath, setCustomPath] = useState("");

  useEffect(() => {
    if (!open || !tab) return;
    setTitle(tab.title);
    setCustomPath(tab.workspaceRoot?.trim() ?? "");
    if (tab.workspaceId) {
      setPresetKey(`id:${tab.workspaceId}`);
    } else if (tab.workspaceRoot) {
      const match = workspaces.find(
        (w) =>
          w.rootPath &&
          w.rootPath.replace(/[/\\]+$/, "").toLowerCase() ===
            tab.workspaceRoot!.replace(/[/\\]+$/, "").toLowerCase(),
      );
      setPresetKey(match?.id ? `id:${match.id}` : "custom");
    } else {
      setPresetKey("");
    }
  }, [open, tab, workspaces]);

  if (!tab) return null;

  return (
    <Modal
      open={open}
      title="房间设置"
      onClose={onClose}
      footer={
        <>
          {onOpenAgentCaps ? (
            <button
              type="button"
              className="mr-auto rounded-xl border border-slate-200 px-4 py-2 text-sm text-slate-700 transition hover:bg-slate-50 dark:border-zinc-700 dark:text-zinc-200 dark:hover:bg-zinc-800"
              onClick={onOpenAgentCaps}
              disabled={saving}
            >
              Agent 能力
            </button>
          ) : null}
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
              const trimmedPath = customPath.trim();
              if (!trimmedPath) {
                onSave({ title: title.trim(), workspaceId: null, workspaceRoot: null });
                return;
              }

              if (presetKey.startsWith("id:")) {
                const workspaceId = presetKey.slice(3);
                const match = workspaces.find((w) => w.id === workspaceId);
                const root = match?.rootPath?.trim() ?? trimmedPath;
                const sameRoot =
                  root.replace(/[/\\]+$/, "").toLowerCase() ===
                  trimmedPath.replace(/[/\\]+$/, "").toLowerCase();
                onSave({
                  title: title.trim(),
                  workspaceId: sameRoot ? workspaceId : null,
                  workspaceRoot: trimmedPath,
                });
                return;
              }

              onSave({
                title: title.trim(),
                workspaceId: null,
                workspaceRoot: trimmedPath,
              });
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
        @agent 将在此路径读写文件。可选手动输入本机目录，例如 C:\xxxx。留空则使用 Athlon 当前活动工作区。
      </p>
      <select
        className="mb-3 w-full rounded-2xl border border-slate-200 bg-slate-50 px-3 py-2.5 text-sm text-slate-900 outline-none focus:border-sky-300 focus:ring-2 focus:ring-sky-100 dark:border-zinc-700 dark:bg-zinc-800 dark:text-zinc-100"
        value={presetKey === "custom" ? "custom" : presetKey}
        onChange={(e) => {
          const next = e.target.value;
          setPresetKey(next);
          if (!next) {
            setCustomPath("");
            return;
          }
          if (next === "custom") {
            return;
          }
          if (next.startsWith("id:")) {
            const id = next.slice(3);
            const match = workspaces.find((w) => w.id === id);
            if (match?.rootPath) setCustomPath(match.rootPath);
          }
        }}
      >
        <option value="">使用当前活动工作区</option>
        {workspaces.map((w) => (
          <option key={w.id || w.rootPath} value={w.id ? `id:${w.id}` : `root:${w.rootPath}`}>
            {w.name}
            {w.rootPath ? ` — ${w.rootPath}` : ""}
          </option>
        ))}
        <option value="custom">自定义路径…</option>
      </select>
      <input
        className="w-full rounded-2xl border border-slate-200 bg-slate-50 px-3 py-2.5 font-mono text-sm text-slate-900 outline-none placeholder:font-sans placeholder:text-slate-400 focus:border-sky-300 focus:ring-2 focus:ring-sky-100 dark:border-zinc-700 dark:bg-zinc-800 dark:text-zinc-100 dark:placeholder:text-zinc-500"
        placeholder="C:\xxxx 或 D:\projects\foo"
        value={customPath}
        onChange={(e) => {
          setCustomPath(e.target.value);
          setPresetKey(e.target.value.trim() ? "custom" : "");
        }}
      />
      {error ? <p className="mt-3 text-sm text-red-500">{error}</p> : null}
    </Modal>
  );
}
