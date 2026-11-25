export interface RoomAttachment {
  id: string;
  fileName: string;
  mimeType: string;
  size: number;
  isImage: boolean;
  storedName?: string;
  previewUrl?: string;
}

export interface RoomMessage {
  id: string;
  senderId: string;
  displayName: string;
  kind: "text" | "image" | "file" | "agent" | string;
  text?: string | null;
  attachments: RoomAttachment[];
  /** Stable senderIds of teammates @-mentioned in this message. */
  mentionedSenderIds?: string[];
  createdAt: string;
}

export type AgentRuntimeMode = "local" | "sandbox" | "cloud_sandbox";

export interface RoomMeta {
  id: string;
  title: string;
  createdAt: string;
  workspaceRoot?: string | null;
  workspaceId?: string | null;
  workspaceName?: string | null;
  agentRuntimeMode?: AgentRuntimeMode | string | null;
}

export interface PendingFile {
  id: string;
  file: File;
  previewUrl?: string;
}

export type RoomRole = "host" | "guest";

export interface RoomTab {
  id: string;
  title: string;
  baseUrl: string;
  token: string;
  role: RoomRole;
  unread: number;
  inviteUrl: string;
  workspaceRoot?: string | null;
  workspaceId?: string | null;
  workspaceName?: string | null;
  agentRuntimeMode?: AgentRuntimeMode | string | null;
}

export interface SandboxProbeSlice {
  available: boolean;
  imageReady?: boolean;
  dockerVersion?: string | null;
  message?: string | null;
  quotaHint?: string | null;
  runtimeHints?: string[];
}

export interface SandboxStatus extends SandboxProbeSlice {
  /** @deprecated prefer localDocker */
  available: boolean;
  imageReady: boolean;
  localDocker?: SandboxProbeSlice;
  cloud?: SandboxProbeSlice;
  recommended?: AgentRuntimeMode | string | null;
}

export interface WorkspaceItem {
  id: string;
  name: string;
  rootPath: string;
  kind?: string;
}

export interface RoomAgentProfileData {
  toolAllowList: string[];
  skillNames: string[];
  mcpServerNames: string[];
}

export interface RoomAgentCatalog {
  tools: string[];
  skills: Array<{ name: string }>;
  mcpServers: Array<{ name: string }>;
}

export interface RoomAgentProfileResponse {
  profile: RoomAgentProfileData;
  catalog: RoomAgentCatalog;
}

export interface AgentTraceStep {
  id: string;
  messageId: string;
  type:
    | "run_started"
    | "tool_start"
    | "tool_args"
    | "tool_result"
    | "reasoning"
    | "run_finished"
    | "cancelled"
    | "error"
    | string;
  toolCallId?: string | null;
  toolName?: string | null;
  summary?: string | null;
  at: string;
}

export interface RoomPresenceMember {
  senderId: string;
  displayName: string;
  joinedAt: string;
}

export interface RoomPresenceSnapshot {
  roomId: string;
  title?: string;
  members: RoomPresenceMember[];
  count: number;
}

export interface RoomSessionState {
  messages: RoomMessage[];
  draft: string;
  pending: PendingFile[];
  meta: RoomMeta | null;
  seenIds: Set<string>;
  error: string | null;
}
