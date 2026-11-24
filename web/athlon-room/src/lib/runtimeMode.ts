import type { AgentRuntimeMode } from "../types";

export function normalizeAgentRuntimeMode(
  mode: string | null | undefined,
): AgentRuntimeMode {
  const trimmed = (mode ?? "").trim().toLowerCase();
  if (trimmed === "sandbox") return "sandbox";
  if (trimmed === "cloud_sandbox" || trimmed === "cloud") return "cloud_sandbox";
  return "local";
}

export function agentRuntimeModeLabel(mode: string | null | undefined): string {
  switch (normalizeAgentRuntimeMode(mode)) {
    case "sandbox":
      return "Sandbox";
    case "cloud_sandbox":
      return "云沙箱";
    default:
      return "本地";
  }
}
