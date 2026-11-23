import type { RoomRole, RoomTab } from "../types";

const KEY = "athlon-room-recent-v1";
const MAX = 24;

export interface RecentRoomRecord {
  id: string;
  title: string;
  baseUrl: string;
  token: string;
  role: RoomRole;
  inviteUrl: string;
  lastOpened: string;
}

export function loadRecentRooms(): RecentRoomRecord[] {
  try {
    const raw = localStorage.getItem(KEY);
    if (!raw) return [];
    const parsed = JSON.parse(raw) as RecentRoomRecord[];
    return Array.isArray(parsed) ? parsed : [];
  } catch {
    return [];
  }
}

export function saveRecentRoom(tab: RoomTab): void {
  const next: RecentRoomRecord = {
    id: tab.id,
    title: tab.title,
    baseUrl: tab.baseUrl,
    token: tab.token,
    role: tab.role,
    inviteUrl: tab.inviteUrl,
    lastOpened: new Date().toISOString(),
  };
  const existing = loadRecentRooms().filter(
    (r) => !(r.id === tab.id && r.baseUrl === tab.baseUrl),
  );
  existing.unshift(next);
  localStorage.setItem(KEY, JSON.stringify(existing.slice(0, MAX)));
}

export function removeRecentRoom(id: string, baseUrl: string): void {
  const next = loadRecentRooms().filter((r) => !(r.id === id && r.baseUrl === baseUrl));
  localStorage.setItem(KEY, JSON.stringify(next));
}

export function recentToTab(r: RecentRoomRecord): RoomTab {
  return {
    id: r.id,
    title: r.title,
    baseUrl: r.baseUrl,
    token: r.token,
    role: r.role,
    unread: 0,
    inviteUrl: r.inviteUrl,
  };
}
