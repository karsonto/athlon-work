import { useSyncExternalStore } from "react";

function getSnapshot(): boolean {
  const root = document.documentElement;
  return root.classList.contains("dark") || root.getAttribute("data-theme") === "dark";
}

function subscribe(onChange: () => void): () => void {
  const obs = new MutationObserver(onChange);
  obs.observe(document.documentElement, {
    attributes: true,
    attributeFilter: ["class", "data-theme"],
  });
  return () => obs.disconnect();
}

/** 与 App/DaisyUI 主题同步，供 Recharts 配色。 */
export function useChartIsDark(): boolean {
  return useSyncExternalStore(subscribe, getSnapshot, getSnapshot);
}
