import { useEffect, useState } from "react";
import type { AgentRuntimeMode, RoomTab, SandboxStatus, WorkspaceItem } from "../types";
import { fetchSandboxStatus } from "../lib/api";
import { normalizeAgentRuntimeMode } from "../lib/runtimeMode";
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
    agentRuntimeMode: AgentRuntimeMode;
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
  const [runtimeMode, setRuntimeMode] = useState<AgentRuntimeMode>("local");
  const [sandboxStatus, setSandboxStatus] = useState<SandboxStatus | null>(null);
  const [sandboxLoading, setSandboxLoading] = useState(false);

  useEffect(() => {
    if (!open || !tab) return;
    setTitle(tab.title);
    setCustomPath(tab.workspaceRoot?.trim() ?? "");
    setRuntimeMode(normalizeAgentRuntimeMode(tab.agentRuntimeMode));
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

  useEffect(() => {
    if (!open) return;
    let cancelled = false;
    setSandboxLoading(true);
    void fetchSandboxStatus()
      .then((s) => {
        if (!cancelled) setSandboxStatus(s);
      })
      .catch(() => {
        if (!cancelled) {
          setSandboxStatus({
            available: false,
            imageReady: false,
            message: "无法探测沙箱状态",
            localDocker: { available: false, message: "无法探测 Docker 状态" },
            cloud: { available: false, message: "无法探测云沙箱状态" },
          });
        }
      })
      .finally(() => {
        if (!cancelled) setSandboxLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [open]);

  if (!tab) return null;

  const localDocker = sandboxStatus?.localDocker ?? sandboxStatus;
  const cloud = sandboxStatus?.cloud;
  const sandboxDisabled = sandboxLoading || !localDocker?.available;
  const cloudDisabled = sandboxLoading || !cloud?.available;
  const preferCloud =
    !!cloud?.available && !localDocker?.available;

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
            disabled={
              saving ||
              !title.trim() ||
              !customPath.trim() ||
              (runtimeMode === "sandbox" && sandboxDisabled) ||
              (runtimeMode === "cloud_sandbox" && cloudDisabled)
            }
            onClick={() => {
              const trimmedPath = customPath.trim();
              if (!trimmedPath) {
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
                  agentRuntimeMode: runtimeMode,
                });
                return;
              }

              onSave({
                title: title.trim(),
                workspaceId: null,
                workspaceRoot: trimmedPath,
                agentRuntimeMode: runtimeMode,
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
        新建房间不会自动带入工作区，请在此选择或手输本机路径（例如 C:\xxxx）。@agent
        与聊天上传都会使用该目录。
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
        <option value="">请选择或手输路径</option>
        {workspaces.map((w) => (
          <option key={w.id || w.rootPath} value={w.id ? `id:${w.id}` : `root:${w.rootPath}`}>
            {w.name}
            {w.rootPath ? ` — ${w.rootPath}` : ""}
          </option>
        ))}
        <option value="custom">自定义路径…</option>
      </select>
      <input
        className="mb-4 w-full rounded-2xl border border-slate-200 bg-slate-50 px-3 py-2.5 font-mono text-sm text-slate-900 outline-none placeholder:font-sans placeholder:text-slate-400 focus:border-sky-300 focus:ring-2 focus:ring-sky-100 dark:border-zinc-700 dark:bg-zinc-800 dark:text-zinc-100 dark:placeholder:text-zinc-500"
        placeholder="C:\xxxx 或 D:\projects\foo"
        value={customPath}
        onChange={(e) => {
          setCustomPath(e.target.value);
          setPresetKey(e.target.value.trim() ? "custom" : "");
        }}
      />

      <label className="mb-1 block text-sm font-medium text-slate-700 dark:text-zinc-200">
        Agent 执行环境
      </label>
      <p className="mb-2 text-xs text-slate-500 dark:text-zinc-400">
        三选一：本地、本机 Docker Sandbox，或 Athlon 云沙箱（无 Docker 时可用；工作区会同步至云端）。
      </p>
      <div className="mb-2 flex flex-col gap-2">
        <label className="flex cursor-pointer items-start gap-2 rounded-2xl border border-slate-200 px-3 py-2.5 text-sm dark:border-zinc-700">
          <input
            type="radio"
            className="mt-1"
            name="agentRuntimeMode"
            checked={runtimeMode === "local"}
            onChange={() => setRuntimeMode("local")}
          />
          <span>
            <span className="font-medium text-slate-800 dark:text-zinc-100">本地运行</span>
            <span className="mt-0.5 block text-xs text-slate-500 dark:text-zinc-400">
              文件与命令在宿主机执行
            </span>
          </span>
        </label>
        <label
          className={`flex items-start gap-2 rounded-2xl border border-slate-200 px-3 py-2.5 text-sm dark:border-zinc-700 ${
            sandboxDisabled ? "cursor-not-allowed opacity-60" : "cursor-pointer"
          }`}
        >
          <input
            type="radio"
            className="mt-1"
            name="agentRuntimeMode"
            checked={runtimeMode === "sandbox"}
            disabled={sandboxDisabled}
            onChange={() => setRuntimeMode("sandbox")}
          />
          <span>
            <span className="font-medium text-slate-800 dark:text-zinc-100">Docker Sandbox</span>
            <span className="mt-0.5 block text-xs text-slate-500 dark:text-zinc-400">
              本机容器：Python 3.12 / Java 8 / Node 20 / Chromium
            </span>
          </span>
        </label>
        <label
          className={`flex items-start gap-2 rounded-2xl border px-3 py-2.5 text-sm ${
            preferCloud
              ? "border-sky-300 bg-sky-50/60 dark:border-sky-700 dark:bg-sky-950/40"
              : "border-slate-200 dark:border-zinc-700"
          } ${cloudDisabled ? "cursor-not-allowed opacity-60" : "cursor-pointer"}`}
        >
          <input
            type="radio"
            className="mt-1"
            name="agentRuntimeMode"
            checked={runtimeMode === "cloud_sandbox"}
            disabled={cloudDisabled}
            onChange={() => setRuntimeMode("cloud_sandbox")}
          />
          <span>
            <span className="font-medium text-slate-800 dark:text-zinc-100">云沙箱</span>
            <span className="mt-0.5 block text-xs text-slate-500 dark:text-zinc-400">
              远程隔离环境；工作区与勾选 Skills 将上传同步至 Athlon 云端
            </span>
          </span>
        </label>
      </div>
      <p className="mb-1 text-xs text-slate-500 dark:text-zinc-400">
        {sandboxLoading
          ? "正在检测 Docker / 云沙箱…"
          : runtimeMode === "cloud_sandbox"
            ? (cloud?.message ?? "云沙箱需 Imp SSO 登录，且已配置 CloudSandbox:BaseUrl。")
            : runtimeMode === "sandbox"
              ? (localDocker?.message ?? "选择 Docker Sandbox 前请确保 Docker Desktop 已启动。")
              : "本地模式不依赖 Docker 或云沙箱。"}
      </p>
      {runtimeMode === "cloud_sandbox" ? (
        <p className="mb-1 text-xs text-amber-700 dark:text-amber-300">
          启用云沙箱即表示同意将本房间工作区内容同步到 Athlon 云沙箱。
          {cloud?.quotaHint ? ` ${cloud.quotaHint}` : ""}
        </p>
      ) : null}

      {error ? <p className="mt-3 text-sm text-red-500">{error}</p> : null}
    </Modal>
  );
}
