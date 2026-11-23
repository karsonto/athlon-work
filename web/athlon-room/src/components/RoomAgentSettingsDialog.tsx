import { useEffect, useState } from "react";
import type { RoomAgentCatalog, RoomAgentProfileData } from "../types";
import { Modal } from "./Modal";

interface RoomAgentSettingsDialogProps {
  open: boolean;
  profile: RoomAgentProfileData | null;
  catalog: RoomAgentCatalog | null;
  saving: boolean;
  error: string | null;
  onClose: () => void;
  onSave: (profile: RoomAgentProfileData) => void;
}

function toggle(list: string[], name: string, checked: boolean): string[] {
  const next = new Set(list.map((x) => x.trim()).filter(Boolean));
  if (checked) next.add(name);
  else next.delete(name);
  return [...next];
}

function CheckboxGroup({
  title,
  items,
  selected,
  onToggle,
  emptyHint,
}: {
  title: string;
  items: string[];
  selected: string[];
  onToggle: (name: string, checked: boolean) => void;
  emptyHint: string;
}) {
  const selectedSet = new Set(selected.map((x) => x.toLowerCase()));
  return (
    <section className="mb-5 last:mb-0">
      <h3 className="mb-2 text-sm font-semibold text-slate-900 dark:text-zinc-100">{title}</h3>
      {items.length === 0 ? (
        <p className="text-xs text-slate-500 dark:text-zinc-400">{emptyHint}</p>
      ) : (
        <div className="grid gap-2 sm:grid-cols-2">
          {items.map((name) => {
            const checked = selectedSet.has(name.toLowerCase());
            return (
              <label
                key={name}
                className="flex cursor-pointer items-center gap-2 rounded-2xl border border-slate-200 bg-slate-50 px-3 py-2.5 text-sm text-slate-700 transition hover:border-sky-200 hover:bg-sky-50/60 dark:border-zinc-700 dark:bg-zinc-800 dark:text-zinc-200 dark:hover:border-sky-500/40 dark:hover:bg-sky-500/10"
              >
                <input
                  type="checkbox"
                  className="checkbox checkbox-sm checkbox-info"
                  checked={checked}
                  onChange={(e) => onToggle(name, e.target.checked)}
                />
                <span className="min-w-0 truncate">{name}</span>
              </label>
            );
          })}
        </div>
      )}
    </section>
  );
}

export function RoomAgentSettingsDialog({
  open,
  profile,
  catalog,
  saving,
  error,
  onClose,
  onSave,
}: RoomAgentSettingsDialogProps) {
  const [draft, setDraft] = useState<RoomAgentProfileData>({
    toolAllowList: [],
    skillNames: [],
    mcpServerNames: [],
  });

  useEffect(() => {
    if (!open || !profile) return;
    setDraft({
      toolAllowList: [...profile.toolAllowList],
      skillNames: [...profile.skillNames],
      mcpServerNames: [...profile.mcpServerNames],
    });
  }, [open, profile]);

  return (
    <Modal
      open={open}
      title="Agent 能力"
      wide
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
            disabled={saving || !catalog}
            onClick={() => onSave(draft)}
          >
            {saving ? "保存中…" : "保存"}
          </button>
        </>
      }
    >
      <p className="mb-4 text-slate-500 dark:text-zinc-400">
        全局配置房间内 @agent 可用的 Tools、Skills 与 MCP。对所有本机房间生效。
      </p>

      <CheckboxGroup
        title="Tools"
        items={catalog?.tools ?? []}
        selected={draft.toolAllowList}
        emptyHint="暂无可用工具"
        onToggle={(name, checked) =>
          setDraft((prev) => ({
            ...prev,
            toolAllowList: toggle(prev.toolAllowList, name, checked),
          }))
        }
      />

      <CheckboxGroup
        title="Skills"
        items={(catalog?.skills ?? []).map((s) => s.name)}
        selected={draft.skillNames}
        emptyHint="暂无已安装 Skill"
        onToggle={(name, checked) =>
          setDraft((prev) => ({
            ...prev,
            skillNames: toggle(prev.skillNames, name, checked),
          }))
        }
      />

      <CheckboxGroup
        title="MCP"
        items={(catalog?.mcpServers ?? []).map((s) => s.name)}
        selected={draft.mcpServerNames}
        emptyHint="暂无已启用的 MCP 服务"
        onToggle={(name, checked) =>
          setDraft((prev) => ({
            ...prev,
            mcpServerNames: toggle(prev.mcpServerNames, name, checked),
          }))
        }
      />

      {error ? <p className="mt-2 text-sm text-red-500">{error}</p> : null}
    </Modal>
  );
}
