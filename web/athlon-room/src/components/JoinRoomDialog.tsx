import { Modal } from "./Modal";

interface JoinRoomDialogProps {
  open: boolean;
  value: string;
  error: string | null;
  onChange: (v: string) => void;
  onClose: () => void;
  onSubmit: () => void;
}

export function JoinRoomDialog({
  open,
  value,
  error,
  onChange,
  onClose,
  onSubmit,
}: JoinRoomDialogProps) {
  return (
    <Modal
      open={open}
      title="加入房间"
      onClose={onClose}
      footer={
        <>
          <button
            type="button"
            className="rounded-xl px-4 py-2 text-sm text-slate-600 transition hover:bg-slate-100 dark:text-zinc-300 dark:hover:bg-zinc-800"
            onClick={onClose}
          >
            取消
          </button>
          <button
            type="button"
            className="rounded-xl bg-sky-600 px-4 py-2 text-sm font-medium text-white transition hover:bg-sky-700"
            onClick={onSubmit}
          >
            加入
          </button>
        </>
      }
    >
      <p className="mb-3 text-slate-500 dark:text-zinc-400">
        粘贴完整邀请链接（含房间 ID 与 token）
      </p>
      <textarea
        className="min-h-[88px] w-full resize-y rounded-2xl border border-slate-200 bg-slate-50 px-3 py-2.5 text-sm text-slate-900 outline-none focus:border-sky-300 focus:ring-2 focus:ring-sky-100 dark:border-zinc-700 dark:bg-zinc-800 dark:text-zinc-100 dark:focus:border-sky-500 dark:focus:ring-sky-500/20"
        placeholder="http://192.168.x.x:6890/r/xxxx?t=..."
        value={value}
        autoFocus
        onChange={(e) => onChange(e.target.value)}
      />
      {error ? <p className="mt-2 text-sm text-red-500">{error}</p> : null}
    </Modal>
  );
}
