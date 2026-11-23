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
  if (!open) return null;

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/55 p-4">
      <form
        className="w-full max-w-md rounded-2xl bg-[#1e1e1e] p-5 shadow-xl ring-1 ring-white/10"
        onSubmit={(e) => {
          e.preventDefault();
          onSubmit();
        }}
      >
        <h2 className="text-base font-semibold text-[var(--text-primary)]">加入房间</h2>
        <p className="mt-1 text-sm text-[var(--text-muted)]">
          粘贴完整邀请链接（含房间 ID 与 token）
        </p>
        <textarea
          className="mt-4 min-h-[88px] w-full resize-y rounded-xl border border-white/10 bg-[#2c2c2c] px-3 py-2.5 text-sm text-[var(--text-primary)] outline-none focus:border-[#12b7f5]/60"
          placeholder="http://192.168.x.x:6890/r/xxxx?t=..."
          value={value}
          autoFocus
          onChange={(e) => onChange(e.target.value)}
        />
        {error ? <p className="mt-2 text-sm text-red-400">{error}</p> : null}
        <div className="mt-4 flex justify-end gap-2">
          <button
            type="button"
            className="rounded-xl px-4 py-2 text-sm text-[var(--text-muted)] hover:bg-white/5"
            onClick={onClose}
          >
            取消
          </button>
          <button
            type="submit"
            className="rounded-xl bg-[#12b7f5] px-4 py-2 text-sm font-medium text-white"
          >
            加入
          </button>
        </div>
      </form>
    </div>
  );
}
