const THEME_STORAGE_KEY = "athlonThemeMode";

export function readStoredDarkMode(): boolean {
  try {
    const stored = window.localStorage.getItem(THEME_STORAGE_KEY);
    if (stored === "dark") {
      return true;
    }
    if (stored === "light") {
      return false;
    }
  } catch {
    // ignore
  }

  return window.matchMedia?.("(prefers-color-scheme: dark)").matches ?? false;
}

export function applyDarkMode(isDark: boolean): void {
  document.documentElement.classList.toggle("dark", isDark);
  document.documentElement.setAttribute("data-theme", isDark ? "dark" : "light");
  try {
    window.localStorage.setItem(THEME_STORAGE_KEY, isDark ? "dark" : "light");
  } catch {
    // ignore
  }
}
