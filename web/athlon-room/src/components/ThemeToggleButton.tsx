import { Moon, Sun } from "lucide-react";

interface ThemeToggleButtonProps {
  isDarkMode: boolean;
  onToggle: () => void;
  /** Compact icon-only for the collapsed sidebar rail. */
  compact?: boolean;
  className?: string;
}

export function ThemeToggleButton({
  isDarkMode,
  onToggle,
  compact = false,
  className = "",
}: ThemeToggleButtonProps) {
  const label = isDarkMode ? "切换浅色" : "切换深色";
  return (
    <button
      type="button"
      className={[
        "inline-flex items-center justify-center rounded-xl border border-slate-200 text-sm text-slate-600 transition hover:border-slate-300 hover:bg-slate-50 hover:text-slate-900 dark:border-zinc-700 dark:text-zinc-300 dark:hover:border-zinc-600 dark:hover:bg-zinc-800 dark:hover:text-white",
        compact ? "h-9 w-9" : "gap-2 px-3 py-2",
        className,
      ].join(" ")}
      onClick={onToggle}
      aria-label={label}
      title={label}
    >
      {isDarkMode ? <Sun size={16} /> : <Moon size={16} />}
      {compact ? null : <span>{isDarkMode ? "浅色" : "深色"}</span>}
    </button>
  );
}
