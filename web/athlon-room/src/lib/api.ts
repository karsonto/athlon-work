import type {
  RoomAgentProfileData,
  RoomAgentProfileResponse,
  RoomAttachment,
  RoomMessage,
  RoomMeta,
  WorkspaceItem,
} from "../types";

export function apiUrl(baseUrl: string, path: string): string {
  const base = baseUrl.replace(/\/$/, "");
  const p = path.startsWith("/") ? path : `/${path}`;
  return `${base}${p}`;
}

export function parseInviteInput(raw: string): {
  baseUrl: string;
  roomId: string;
  token: string;
} | null {
  const text = raw.trim();
  if (!text) return null;

  try {
    const url = new URL(text);
    const match = url.pathname.match(/\/r\/([A-Za-z0-9_-]+)/);
    const token = url.searchParams.get("t") ?? "";
    if (!match || !token) return null;
    return {
      baseUrl: `${url.protocol}//${url.host}`,
      roomId: match[1],
      token,
    };
  } catch {
    return null;
  }
}

export function parseRoomFromLocation(): {
  baseUrl: string;
  roomId: string;
  token: string;
} | null {
  const params = new URLSearchParams(window.location.search);
  const token = params.get("t") ?? "";
  const hostOverride = params.get("host");
  const match = window.location.pathname.match(/\/r\/([A-Za-z0-9_-]+)/);
  if (!match || !token) {
    return null;
  }

  const baseUrl = hostOverride
    ? hostOverride.replace(/\/$/, "")
    : `${window.location.protocol}//${window.location.host}`;
  return { baseUrl, roomId: match[1], token };
}

function authHeaders(token: string, extra?: HeadersInit): HeadersInit {
  return {
    Authorization: `Bearer ${token}`,
    ...extra,
  };
}

async function readError(res: Response): Promise<string> {
  try {
    const data = await res.json();
    return data.error ?? res.statusText;
  } catch {
    return res.statusText || `HTTP ${res.status}`;
  }
}

/** Prefer loopback for host-only manage APIs (list/create/close). */
export function localManageBaseUrl(): string {
  try {
    const u = new URL(window.location.href);
    if (u.hostname === "127.0.0.1" || u.hostname === "localhost") {
      return u.origin;
    }
    return `${u.protocol}//127.0.0.1:${u.port || "6890"}`;
  } catch {
    return "http://127.0.0.1:6890";
  }
}

export async function fetchLocalRooms(baseUrl = ""): Promise<{
  lanIp?: string;
  port?: number;
  rooms: Array<{ id: string; title: string; inviteUrl: string; createdAt: string }>;
}> {
  const res = await fetch(apiUrl(baseUrl || localManageBaseUrl(), "/api/rooms"));
  if (!res.ok) {
    throw new Error(await readError(res));
  }
  return res.json();
}

export async function createLocalRoom(
  title?: string,
  baseUrl = "",
): Promise<{
  id: string;
  title: string;
  inviteUrl: string;
  lanIp: string;
  port: number;
  token: string;
  createdAt: string;
}> {
  const res = await fetch(apiUrl(baseUrl || localManageBaseUrl(), "/api/rooms"), {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ title: title ?? "Athlon 聊天房间" }),
  });
  if (!res.ok) {
    throw new Error(await readError(res));
  }
  return res.json();
}

export async function closeLocalRoom(roomId: string, baseUrl = ""): Promise<void> {
  const res = await fetch(
    apiUrl(baseUrl || localManageBaseUrl(), `/api/rooms/${roomId}/close`),
    { method: "POST" },
  );
  if (!res.ok) {
    throw new Error(await readError(res));
  }
}

export async function renameLocalRoom(
  roomId: string,
  title: string,
  baseUrl = "",
): Promise<{ id: string; title: string }> {
  const res = await fetch(
    apiUrl(baseUrl || localManageBaseUrl(), `/api/rooms/${roomId}/title`),
    {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ title }),
    },
  );
  if (!res.ok) {
    throw new Error(await readError(res));
  }
  return res.json();
}

export async function setLocalRoomWorkspace(
  roomId: string,
  body: { workspaceId?: string | null; workspaceRoot?: string | null },
  baseUrl = "",
): Promise<{
  id: string;
  workspaceRoot?: string | null;
  workspaceId?: string | null;
  workspaceName?: string | null;
}> {
  const res = await fetch(
    apiUrl(baseUrl || localManageBaseUrl(), `/api/rooms/${roomId}/workspace`),
    {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    },
  );
  if (!res.ok) {
    throw new Error(await readError(res));
  }
  return res.json();
}

export async function fetchWorkspaces(baseUrl = ""): Promise<WorkspaceItem[]> {
  const res = await fetch(apiUrl(baseUrl || localManageBaseUrl(), "/api/workspaces"));
  if (!res.ok) {
    throw new Error(await readError(res));
  }
  const data = await res.json();
  return data.workspaces ?? [];
}

export async function fetchRoomAgentProfile(
  baseUrl = "",
): Promise<RoomAgentProfileResponse> {
  const res = await fetch(apiUrl(baseUrl || localManageBaseUrl(), "/api/room-agent/profile"));
  if (!res.ok) {
    throw new Error(await readError(res));
  }
  return res.json();
}

export async function saveRoomAgentProfile(
  profile: RoomAgentProfileData,
  baseUrl = "",
): Promise<RoomAgentProfileResponse> {
  const res = await fetch(apiUrl(baseUrl || localManageBaseUrl(), "/api/room-agent/profile"), {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ profile }),
  });
  if (!res.ok) {
    throw new Error(await readError(res));
  }
  return res.json();
}

export async function fetchRoom(
  baseUrl: string,
  roomId: string,
  token: string,
): Promise<RoomMeta> {
  const res = await fetch(
    apiUrl(baseUrl, `/api/rooms/${roomId}?t=${encodeURIComponent(token)}`),
    { headers: authHeaders(token) },
  );
  if (!res.ok) {
    throw new Error(await readError(res));
  }
  return res.json();
}

export async function fetchMessages(
  baseUrl: string,
  roomId: string,
  token: string,
  after?: string,
): Promise<RoomMessage[]> {
  const qs = new URLSearchParams({ t: token });
  if (after) qs.set("after", after);
  const res = await fetch(apiUrl(baseUrl, `/api/rooms/${roomId}/messages?${qs}`), {
    headers: authHeaders(token),
  });
  if (!res.ok) {
    throw new Error(await readError(res));
  }
  const data = await res.json();
  return data.messages ?? [];
}

export async function uploadAttachment(
  baseUrl: string,
  roomId: string,
  token: string,
  file: File,
): Promise<RoomAttachment> {
  const form = new FormData();
  form.append("file", file, file.name);
  const res = await fetch(
    apiUrl(baseUrl, `/api/rooms/${roomId}/attachments?t=${encodeURIComponent(token)}`),
    {
      method: "POST",
      headers: authHeaders(token),
      body: form,
    },
  );
  if (!res.ok) {
    throw new Error(await readError(res));
  }
  return res.json();
}

export async function postMessage(
  baseUrl: string,
  roomId: string,
  token: string,
  body: {
    senderId: string;
    displayName: string;
    text?: string;
    attachmentIds?: string[];
  },
): Promise<RoomMessage> {
  const res = await fetch(
    apiUrl(baseUrl, `/api/rooms/${roomId}/messages?t=${encodeURIComponent(token)}`),
    {
      method: "POST",
      headers: authHeaders(token, { "Content-Type": "application/json" }),
      body: JSON.stringify(body),
    },
  );
  if (!res.ok) {
    throw new Error(await readError(res));
  }
  return res.json();
}

export function attachmentUrl(
  baseUrl: string,
  roomId: string,
  token: string,
  attachmentId: string,
): string {
  return apiUrl(
    baseUrl,
    `/api/rooms/${roomId}/attachments/${attachmentId}?t=${encodeURIComponent(token)}`,
  );
}

export function connectEvents(
  baseUrl: string,
  roomId: string,
  token: string,
  onMessage: (msg: RoomMessage) => void,
  onMessageUpdated?: (msg: RoomMessage) => void,
  onError?: (err: Event) => void,
): EventSource {
  const url = apiUrl(baseUrl, `/api/rooms/${roomId}/events?t=${encodeURIComponent(token)}`);
  const es = new EventSource(url);
  es.addEventListener("message", (ev) => {
    try {
      onMessage(JSON.parse((ev as MessageEvent).data) as RoomMessage);
    } catch {
      // ignore
    }
  });
  es.addEventListener("message_updated", (ev) => {
    try {
      onMessageUpdated?.(JSON.parse((ev as MessageEvent).data) as RoomMessage);
    } catch {
      // ignore
    }
  });
  es.onerror = (ev) => onError?.(ev);
  return es;
}

export function formatBytes(size: number): string {
  if (size < 1024) return `${size} B`;
  if (size < 1024 * 1024) return `${(size / 1024).toFixed(1)} KB`;
  return `${(size / (1024 * 1024)).toFixed(1)} MB`;
}

/**
 * UUID / id helper that works outside secure contexts.
 * `crypto.randomUUID` is missing on http://{LAN-IP} (non-localhost HTTP).
 */
export function newId(): string {
  const c = globalThis.crypto;
  if (c && typeof c.randomUUID === "function") {
    return c.randomUUID();
  }

  if (c && typeof c.getRandomValues === "function") {
    const bytes = new Uint8Array(16);
    c.getRandomValues(bytes);
    bytes[6] = (bytes[6]! & 0x0f) | 0x40;
    bytes[8] = (bytes[8]! & 0x3f) | 0x80;
    const hex = [...bytes].map((b) => b.toString(16).padStart(2, "0")).join("");
    return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
  }

  return "xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx".replace(/[xy]/g, (ch) => {
    const r = (Math.random() * 16) | 0;
    const v = ch === "x" ? r : (r & 0x3) | 0x8;
    return v.toString(16);
  });
}

export function createSenderId(): string {
  const key = "athlon-room-sender-id";
  const existing = localStorage.getItem(key);
  if (existing) return existing;
  const id = newId().replace(/-/g, "").slice(0, 12);
  localStorage.setItem(key, id);
  return id;
}
