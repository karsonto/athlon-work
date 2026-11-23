import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  connectEvents,
  createSenderId,
  fetchMessages,
  fetchRoom,
  parseRoomFromLocation,
  postMessage,
  uploadAttachment,
} from "./lib/api";
import type { PendingFile, RoomMessage, RoomMeta } from "./types";
import { ComposerBar } from "./components/ComposerBar";
import { Lightbox } from "./components/Lightbox";
import { MessageBubble } from "./components/MessageBubble";

const NICK_KEY = "athlon-room-nickname";

export default function App() {
  const route = useMemo(() => parseRoomFromLocation(), []);
  const senderId = useMemo(() => createSenderId(), []);
  const [nickname, setNickname] = useState(() => localStorage.getItem(NICK_KEY) ?? "");
  const [joined, setJoined] = useState(false);
  const [room, setRoom] = useState<RoomMeta | null>(null);
  const [messages, setMessages] = useState<RoomMessage[]>([]);
  const [draft, setDraft] = useState("");
  const [pending, setPending] = useState<PendingFile[]>([]);
  const [sending, setSending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [lightbox, setLightbox] = useState<{ src: string; alt: string } | null>(null);
  const listRef = useRef<HTMLDivElement>(null);
  const seenIds = useRef(new Set<string>());

  const upsertMessages = useCallback((incoming: RoomMessage[]) => {
    setMessages((prev) => {
      let changed = false;
      const next = [...prev];
      for (const msg of incoming) {
        if (seenIds.current.has(msg.id)) continue;
        seenIds.current.add(msg.id);
        next.push(msg);
        changed = true;
      }
      if (!changed) return prev;
      next.sort((a, b) => a.createdAt.localeCompare(b.createdAt));
      return next;
    });
  }, []);

  useEffect(() => {
    if (!joined || !route) return;
    let cancelled = false;
    let es: EventSource | null = null;

    (async () => {
      try {
        const meta = await fetchRoom(route.roomId, route.token);
        if (cancelled) return;
        setRoom(meta);
        const history = await fetchMessages(route.roomId, route.token);
        if (cancelled) return;
        for (const m of history) seenIds.current.add(m.id);
        setMessages(history);
        es = connectEvents(route.roomId, route.token, (msg) => upsertMessages([msg]));
      } catch (e) {
        if (!cancelled) {
          setError(e instanceof Error ? e.message : "无法进入房间");
        }
      }
    })();

    return () => {
      cancelled = true;
      es?.close();
    };
  }, [joined, route, upsertMessages]);

  useEffect(() => {
    const el = listRef.current;
    if (!el) return;
    el.scrollTop = el.scrollHeight;
  }, [messages.length]);

  const onPickFiles = (files: FileList | null) => {
    if (!files?.length) return;
    const next: PendingFile[] = [];
    for (const file of Array.from(files)) {
      if (file.size > 25 * 1024 * 1024) {
        setError(`文件过大（上限 25MB）：${file.name}`);
        continue;
      }
      const id = crypto.randomUUID();
      const previewUrl = file.type.startsWith("image/") ? URL.createObjectURL(file) : undefined;
      next.push({ id, file, previewUrl });
    }
    setPending((prev) => [...prev, ...next]);
  };

  const onRemovePending = (id: string) => {
    setPending((prev) => {
      const target = prev.find((p) => p.id === id);
      if (target?.previewUrl) URL.revokeObjectURL(target.previewUrl);
      return prev.filter((p) => p.id !== id);
    });
  };

  const onJoin = (e: React.FormEvent) => {
    e.preventDefault();
    const name = nickname.trim();
    if (!name) {
      setError("请输入昵称");
      return;
    }
    if (!route) {
      setError("无效的房间链接（缺少房间 ID 或邀请令牌）");
      return;
    }
    localStorage.setItem(NICK_KEY, name);
    setError(null);
    setJoined(true);
  };

  const onSend = async () => {
    if (!route || sending) return;
    const text = draft.trim();
    if (!text && pending.length === 0) return;

    setSending(true);
    setError(null);
    try {
      const attachmentIds: string[] = [];
      for (const p of pending) {
        const uploaded = await uploadAttachment(route.roomId, route.token, p.file);
        attachmentIds.push(uploaded.id);
        if (p.previewUrl) URL.revokeObjectURL(p.previewUrl);
      }
      const msg = await postMessage(route.roomId, route.token, {
        senderId,
        displayName: nickname.trim(),
        text: text || undefined,
        attachmentIds: attachmentIds.length ? attachmentIds : undefined,
      });
      upsertMessages([msg]);
      setDraft("");
      setPending([]);
    } catch (e) {
      setError(e instanceof Error ? e.message : "发送失败");
    } finally {
      setSending(false);
    }
  };

  if (!route) {
    return (
      <div className="flex h-full items-center justify-center bg-[var(--room-bg)] p-6">
        <div className="max-w-md rounded-2xl bg-white p-6 text-center shadow-sm">
          <h1 className="text-lg font-semibold">无效邀请链接</h1>
          <p className="mt-2 text-sm text-slate-500">
            请使用房主分享的完整链接（含房间 ID 与 token）。
          </p>
        </div>
      </div>
    );
  }

  if (!joined) {
    return (
      <div className="flex h-full items-center justify-center bg-[var(--room-bg)] p-6">
        <form
          onSubmit={onJoin}
          className="w-full max-w-sm rounded-2xl bg-white p-6 shadow-sm"
        >
          <h1 className="text-center text-lg font-semibold">加入聊天房间</h1>
          <p className="mt-1 text-center text-sm text-slate-500">输入昵称后开始聊天</p>
          <input
            className="mt-5 w-full rounded-xl border border-slate-200 px-3 py-2.5 outline-none focus:border-emerald-400"
            placeholder="你的昵称"
            value={nickname}
            maxLength={32}
            autoFocus
            onChange={(e) => setNickname(e.target.value)}
          />
          {error ? <p className="mt-2 text-sm text-red-500">{error}</p> : null}
          <button
            type="submit"
            className="mt-4 w-full rounded-xl bg-[#07c160] py-2.5 font-medium text-white"
          >
            进入房间
          </button>
        </form>
      </div>
    );
  }

  return (
    <div className="flex h-full flex-col bg-[var(--room-bg)]">
      <header className="flex items-center justify-between border-b border-black/5 bg-[#2e2e2e] px-4 py-3 text-white">
        <div>
          <div className="text-sm font-medium">{room?.title ?? "Athlon 聊天房间"}</div>
          <div className="text-[11px] text-white/55">以 {nickname} 的身份</div>
        </div>
      </header>

      <div ref={listRef} className="flex-1 space-y-3 overflow-y-auto py-3">
        {messages.length === 0 ? (
          <p className="py-10 text-center text-sm text-slate-400">还没有消息，打个招呼吧</p>
        ) : (
          messages.map((m) => (
            <MessageBubble
              key={m.id}
              message={m}
              isMine={m.senderId === senderId}
              roomId={route.roomId}
              token={route.token}
              onPreviewImage={(src, alt) => setLightbox({ src, alt })}
            />
          ))
        )}
      </div>

      {error ? (
        <div className="bg-red-50 px-4 py-2 text-center text-sm text-red-600">{error}</div>
      ) : null}

      <ComposerBar
        draft={draft}
        pending={pending}
        sending={sending}
        onDraftChange={setDraft}
        onSend={onSend}
        onPickFiles={onPickFiles}
        onRemovePending={onRemovePending}
      />

      {lightbox ? (
        <Lightbox src={lightbox.src} alt={lightbox.alt} onClose={() => setLightbox(null)} />
      ) : null}
    </div>
  );
}
