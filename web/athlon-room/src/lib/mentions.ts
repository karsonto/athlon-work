import type { RoomMessage, RoomPresenceMember } from "../types";

export type MentionCaret = {
  start: number;
  end: number;
  query: string;
};

/** Incomplete @token at caret (query may be empty right after `@`). */
export function findMentionAtCaret(text: string, caret: number): MentionCaret | null {
  const before = text.slice(0, caret);
  const match = before.match(/(^|[\s])(@[^\s@]*)$/);
  if (!match) return null;
  const token = match[2] ?? "";
  const start = before.length - token.length;
  return { start, end: caret, query: token.slice(1) };
}

export function escapeRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

export function insertMentionToken(
  text: string,
  caret: MentionCaret,
  displayName: string,
): { next: string; nextCaret: number } {
  const token = `@${displayName} `;
  const next = `${text.slice(0, caret.start)}${token}${text.slice(caret.end)}`;
  return { next, nextCaret: caret.start + token.length };
}

/** Human teammates only — exclude self and the room agent. */
export function mentionableMembers(
  members: RoomPresenceMember[],
  selfSenderId: string,
): RoomPresenceMember[] {
  const seen = new Set<string>();
  const list: RoomPresenceMember[] = [];
  for (const m of members) {
    if (!m.senderId || !m.displayName?.trim()) continue;
    if (m.senderId === "athlon-agent") continue;
    if (selfSenderId && m.senderId === selfSenderId) continue;
    if (seen.has(m.senderId)) continue;
    seen.add(m.senderId);
    list.push(m);
  }
  return list.sort((a, b) => a.displayName.localeCompare(b.displayName, "zh"));
}

export function filterMentionCandidates(
  members: RoomPresenceMember[],
  query: string,
): RoomPresenceMember[] {
  const q = query.trim().toLowerCase();
  if (!q) return members;
  return members.filter(
    (m) =>
      m.displayName.toLowerCase().includes(q) ||
      m.senderId.toLowerCase().includes(q),
  );
}

export function agentMentionMatches(query: string): boolean {
  const q = query.trim().toLowerCase();
  if (!q) return true;
  return "agent".startsWith(q) || "athlon".startsWith(q);
}

export function resolveMentionedSenderIds(
  text: string,
  members: RoomPresenceMember[],
): string[] {
  if (!text.trim() || members.length === 0) return [];
  const ids = new Set<string>();
  const sorted = [...members]
    .filter((m) => m.senderId && m.displayName?.trim() && m.senderId !== "athlon-agent")
    .sort((a, b) => b.displayName.length - a.displayName.length);

  for (const member of sorted) {
    const token = `@${member.displayName.trim()}`;
    const re = new RegExp(
      `(^|[\\s])${escapeRegExp(token)}(?=$|[\\s,.!?;:，。！？；：])`,
      "g",
    );
    if (re.test(text)) {
      ids.add(member.senderId);
    }
  }
  return [...ids];
}

export function messageMentionsSender(
  msg: RoomMessage,
  senderId: string,
  displayName?: string,
): boolean {
  if (!senderId) return false;
  if (msg.mentionedSenderIds?.some((id) => id === senderId)) return true;

  // Fallback when mention metadata is missing (offline peer / older server).
  const name = displayName?.trim();
  if (!name || !msg.text) return false;
  const token = `@${name}`;
  const re = new RegExp(
    `(^|[\\s])${escapeRegExp(token)}(?=$|[\\s,.!?;:，。！？；：])`,
  );
  return re.test(msg.text);
}

export type TextSegment =
  | { type: "text"; value: string }
  | { type: "mention"; value: string; senderId?: string };

export function splitMentionSegments(
  text: string,
  members: RoomPresenceMember[] = [],
): TextSegment[] {
  if (!text) return [];

  const labels = [
    { token: "@agent", senderId: "athlon-agent" as string | undefined },
    ...[...members]
      .filter((m) => m.displayName?.trim())
      .sort((a, b) => b.displayName.length - a.displayName.length)
      .map((m) => ({ token: `@${m.displayName.trim()}`, senderId: m.senderId as string | undefined })),
  ];

  if (labels.length === 0) {
    return [{ type: "text", value: text }];
  }

  const pattern = labels.map((l) => escapeRegExp(l.token)).join("|");
  const re = new RegExp(`(${pattern})`, "g");
  const parts = text.split(re);
  const byToken = new Map(labels.map((l) => [l.token, l.senderId]));

  return parts
    .filter((p) => p.length > 0)
    .map((part) => {
      if (byToken.has(part)) {
        return { type: "mention" as const, value: part, senderId: byToken.get(part) };
      }
      return { type: "text" as const, value: part };
    });
}

export function previewMentionBody(text: string | null | undefined, max = 80): string {
  const t = (text ?? "").replace(/\s+/g, " ").trim();
  if (!t) return "发来一条消息";
  return t.length > max ? `${t.slice(0, max)}…` : t;
}
