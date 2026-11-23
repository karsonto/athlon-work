import type { RoomAttachment, RoomMessage, RoomMeta } from "../types";

export function parseRoomFromLocation(): { roomId: string; token: string } | null {
  const path = window.location.pathname;
  const match = path.match(/\/r\/([A-Za-z0-9_-]+)/);
  const params = new URLSearchParams(window.location.search);
  const token = params.get("t") ?? "";
  if (!match || !token) {
    return null;
  }
  return { roomId: match[1], token };
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

export async function fetchRoom(roomId: string, token: string): Promise<RoomMeta> {
  const res = await fetch(`/api/rooms/${roomId}?t=${encodeURIComponent(token)}`, {
    headers: authHeaders(token),
  });
  if (!res.ok) {
    throw new Error(await readError(res));
  }
  return res.json();
}

export async function fetchMessages(
  roomId: string,
  token: string,
  after?: string,
): Promise<RoomMessage[]> {
  const qs = new URLSearchParams({ t: token });
  if (after) {
    qs.set("after", after);
  }
  const res = await fetch(`/api/rooms/${roomId}/messages?${qs}`, {
    headers: authHeaders(token),
  });
  if (!res.ok) {
    throw new Error(await readError(res));
  }
  const data = await res.json();
  return data.messages ?? [];
}

export async function uploadAttachment(
  roomId: string,
  token: string,
  file: File,
): Promise<RoomAttachment> {
  const form = new FormData();
  form.append("file", file, file.name);
  const res = await fetch(`/api/rooms/${roomId}/attachments?t=${encodeURIComponent(token)}`, {
    method: "POST",
    headers: authHeaders(token),
    body: form,
  });
  if (!res.ok) {
    throw new Error(await readError(res));
  }
  return res.json();
}

export async function postMessage(
  roomId: string,
  token: string,
  body: {
    senderId: string;
    displayName: string;
    text?: string;
    attachmentIds?: string[];
  },
): Promise<RoomMessage> {
  const res = await fetch(`/api/rooms/${roomId}/messages?t=${encodeURIComponent(token)}`, {
    method: "POST",
    headers: authHeaders(token, { "Content-Type": "application/json" }),
    body: JSON.stringify(body),
  });
  if (!res.ok) {
    throw new Error(await readError(res));
  }
  return res.json();
}

export function attachmentUrl(roomId: string, token: string, attachmentId: string): string {
  return `/api/rooms/${roomId}/attachments/${attachmentId}?t=${encodeURIComponent(token)}`;
}

export function connectEvents(
  roomId: string,
  token: string,
  onMessage: (msg: RoomMessage) => void,
  onError?: (err: Event) => void,
): EventSource {
  const url = `/api/rooms/${roomId}/events?t=${encodeURIComponent(token)}`;
  const es = new EventSource(url);
  es.addEventListener("message", (ev) => {
    try {
      const data = JSON.parse((ev as MessageEvent).data) as RoomMessage;
      onMessage(data);
    } catch {
      // ignore
    }
  });
  es.onerror = (ev) => {
    onError?.(ev);
  };
  return es;
}

export function formatBytes(size: number): string {
  if (size < 1024) return `${size} B`;
  if (size < 1024 * 1024) return `${(size / 1024).toFixed(1)} KB`;
  return `${(size / (1024 * 1024)).toFixed(1)} MB`;
}

export function createSenderId(): string {
  const key = "athlon-room-sender-id";
  const existing = localStorage.getItem(key);
  if (existing) return existing;
  const id = crypto.randomUUID().replace(/-/g, "").slice(0, 12);
  localStorage.setItem(key, id);
  return id;
}
