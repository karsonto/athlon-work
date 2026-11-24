import { Maximize, Minimize, Moon, Sun } from "lucide-react";
import { AthlonIcon } from "./AthlonIcon";

interface AppHeaderProps {
  isFullscreen: boolean;
  isDarkMode: boolean;
  onToggleTheme: () => void;
  onToggleFullscreen: () => void;
}

export function AppHeader({
  isFullscreen,
  isDarkMode,
  onToggleTheme,
  onToggleFullscreen,
}: AppHeaderProps) {
  return (
    <header className="flex h-16 items-center justify-between border-b border-sky-100 bg-white px-6 shadow-sm dark:border-zinc-800 dark:bg-[#18181b] dark:shadow-none">
      <div className="flex items-center gap-3">
        <AthlonIcon className="h-10 w-10 rounded-2xl shadow-sm" />
        <div>
          <h1 className="text-lg font-semibold text-slate-900 dark:text-zinc-100">CodeAthlon</h1>
          <p className="text-sm text-slate-500 dark:text-zinc-400">AI 编程助手</p>
        </div>
      </div>

      <div className="flex items-center gap-2">
        <button
          type="button"
          className="inline-flex items-center gap-2 rounded-xl border border-slate-200 px-3 py-2 text-sm text-slate-600 transition hover:border-slate-300 hover:bg-slate-50 hover:text-slate-900 dark:border-zinc-700 dark:text-zinc-300 dark:hover:border-zinc-600 dark:hover:bg-zinc-800 dark:hover:text-white"
          onClick={onToggleTheme}
          aria-label={isDarkMode ? "切换到浅色模式" : "切换到深色模式"}
          title={isDarkMode ? "切换到浅色模式" : "切换到深色模式"}
        >
          {isDarkMode ? <Sun size={16} /> : <Moon size={16} />}
          {isDarkMode ? "浅色" : "深色"}
        </button>
        <button
          type="button"
          className="inline-flex items-center gap-2 rounded-xl border border-slate-200 px-3 py-2 text-sm text-slate-600 transition hover:border-slate-300 hover:bg-slate-50 hover:text-slate-900 dark:border-zinc-700 dark:text-zinc-300 dark:hover:border-zinc-600 dark:hover:bg-zinc-800 dark:hover:text-white"
          onClick={onToggleFullscreen}
        >
          {isFullscreen ? <Minimize size={16} /> : <Maximize size={16} />}
          {isFullscreen ? "退出全屏" : "全屏"}
        </button>
      </div>
    </header>
  );
}
