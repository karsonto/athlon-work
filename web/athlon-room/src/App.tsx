import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  closeLocalRoom,
  connectEvents,
  createLocalRoom,
  createSenderId,
  fetchLocalRooms,
  fetchMessages,
  fetchRoom,
  parseInviteInput,
  parseRoomFromLocation,
  postMessage,
  uploadAttachment,
} from "./lib/api";
import {
  loadRecentRooms,
  recentToTab,
  removeRecentRoom,
  saveRecentRoom,
} from "./lib/recentRooms";
import type { PendingFile, RoomMessage, RoomMeta, RoomTab } from "./types";
import { ChatPane } from "./components/ChatPane";
import { JoinRoomDialog } from "./components/JoinRoomDialog";
import { Lightbox } from "./components/Lightbox";
import { RoomSidebar, roomKey } from "./components/RoomSidebar";

const NICK_KEY = "athlon-room-nickname";
const MAX_SSE = 8;

interface SessionBundle {
  messages: RoomMessage[];
  draft: string;
  pending: PendingFile[];
  meta: RoomMeta | null;
  seenIds: Set<string>;
  error: string | null;
  loaded: boolean;
}

function emptySession(): SessionBundle {
  return {
    messages: [],
    draft: "",
    pending: [],
    meta: null,
    seenIds: new Set(),
    error: null,
    loaded: false,
  };
}

function sameOriginBase(baseUrl: string): boolean {
  try {
    return new URL(baseUrl).origin === window.location.origin;
  } catch {
    return false;
  }
}

export default function App() {
  const senderId = useMemo(() => createSenderId(), []);
  const [nickname, setNickname] = useState(() => localStorage.getItem(NICK_KEY) ?? "");
  const [joined, setJoined] = useState(false);
  const [rooms, setRooms] = useState<RoomTab[]>([]);
  const [activeKey, setActiveKey] = useState<string | null>(null);
  const [sessions, setSessions] = useState<Record<string, SessionBundle>>({});
  const [sending, setSending] = useState(false);
  const [collapsed, setCollapsed] = useState(false);
  const [joinOpen, setJoinOpen] = useState(false);
  const [joinValue, setJoinValue] = useState("");
  const [joinError, setJoinError] = useState<string | null>(null);
  const [shellError, setShellError] = useState<string | null>(null);
  const [lightbox, setLightbox] = useState<{ src: string; alt: string } | null>(null);
  const [canCreateLocal, setCanCreateLocal] = useState(false);
  const [bootDone, setBootDone] = useState(false);

  const roomsRef = useRef(rooms);
  const activeKeyRef = useRef(activeKey);
  const esMap = useRef(new Map<string, EventSource>());
  const sessionsRef = useRef(sessions);

  useEffect(() => {
    roomsRef.current = rooms;
  }, [rooms]);
  useEffect(() => {
    activeKeyRef.current = activeKey;
  }, [activeKey]);
  useEffect(() => {
    sessionsRef.current = sessions;
  }, [sessions]);

  const upsertTab = useCallback((tab: RoomTab, select = true) => {
    setRooms((prev) => {
      const key = roomKey(tab);
      const idx = prev.findIndex((r) => roomKey(r) === key);
      if (idx >= 0) {
        const next = [...prev];
        next[idx] = { ...next[idx], ...tab, unread: select ? 0 : next[idx].unread };
        return next;
      }
      return [...prev, { ...tab, unread: select ? 0 : tab.unread }];
    });
    setSessions((prev) => {
      const key = roomKey(tab);
      if (prev[key]) return prev;
      return { ...prev, [key]: emptySession() };
    });
    saveRecentRoom(tab);
    if (select) {
      setActiveKey(roomKey(tab));
      setRooms((prev) =>
        prev.map((r) => (roomKey(r) === roomKey(tab) ? { ...r, unread: 0 } : r)),
      );
    }
  }, []);

  const bumpUnread = useCallback((key: string) => {
    if (activeKeyRef.current === key) return;
    setRooms((prev) =>
      prev.map((r) => (roomKey(r) === key ? { ...r, unread: r.unread + 1 } : r)),
    );
  }, []);

  const patchSession = useCallback((key: string, patch: Partial<SessionBundle>) => {
    setSessions((prev) => {
      const cur = prev[key] ?? emptySession();
      return { ...prev, [key]: { ...cur, ...patch } };
    });
  }, []);

  const upsertMessage = useCallback(
    (key: string, msg: RoomMessage) => {
      setSessions((prev) => {
        const cur = prev[key] ?? emptySession();
        if (cur.seenIds.has(msg.id)) {
          const index = cur.messages.findIndex((m) => m.id === msg.id);
          if (index < 0) return prev;
          const messages = [...cur.messages];
          messages[index] = { ...messages[index], ...msg };
          return { ...prev, [key]: { ...cur, messages } };
        }
        const seenIds = new Set(cur.seenIds);
        seenIds.add(msg.id);
        const messages = [...cur.messages, msg].sort((a, b) =>
          a.createdAt.localeCompare(b.createdAt),
        );
        return { ...prev, [key]: { ...cur, messages, seenIds } };
      });
      bumpUnread(key);
    },
    [bumpUnread],
  );

  const ensureSse = useCallback(
    (tab: RoomTab) => {
      const key = roomKey(tab);
      if (esMap.current.has(key)) return;

      while (esMap.current.size >= MAX_SSE) {
        // Evict least recently used among non-active
        const victim = [...esMap.current.keys()].find((k) => k !== activeKeyRef.current);
        if (!victim) break;
        esMap.current.get(victim)?.close();
        esMap.current.delete(victim);
      }

      const es = connectEvents(
        tab.baseUrl,
        tab.id,
        tab.token,
        (msg) => upsertMessage(key, msg),
        (msg) => upsertMessage(key, msg),
      );
      esMap.current.set(key, es);
    },
    [upsertMessage],
  );

  const loadSession = useCallback(
    async (tab: RoomTab) => {
      const key = roomKey(tab);
      const existing = sessionsRef.current[key];
      if (existing?.loaded) {
        ensureSse(tab);
        return;
      }

      try {
        const meta = await fetchRoom(tab.baseUrl, tab.id, tab.token);
        const history = await fetchMessages(tab.baseUrl, tab.id, tab.token);
        const seenIds = new Set(history.map((m) => m.id));
        patchSession(key, {
          meta,
          messages: history,
          seenIds,
          loaded: true,
          error: null,
        });
        setRooms((prev) =>
          prev.map((r) => (roomKey(r) === key ? { ...r, title: meta.title || r.title } : r)),
        );
        ensureSse(tab);
      } catch (e) {
        patchSession(key, {
          error: e instanceof Error ? e.message : "无法进入房间",
          loaded: true,
        });
      }
    },
    [ensureSse, patchSession],
  );

  // Boot: nickname gate + seed rooms from URL / local API / recent
  useEffect(() => {
    let cancelled = false;
    (async () => {
      const seeded: RoomTab[] = [];
      const fromUrl = parseRoomFromLocation();
      if (fromUrl) {
        let title = "聊天房间";
        try {
          const meta = await fetchRoom(fromUrl.baseUrl, fromUrl.roomId, fromUrl.token);
          title = meta.title || title;
        } catch {
          // keep default
        }
        if (cancelled) return;
        seeded.push({
          id: fromUrl.roomId,
          title,
          baseUrl: fromUrl.baseUrl,
          token: fromUrl.token,
          role: sameOriginBase(fromUrl.baseUrl) ? "host" : "guest",
          unread: 0,
          inviteUrl: `${fromUrl.baseUrl}/r/${fromUrl.roomId}?t=${encodeURIComponent(fromUrl.token)}`,
        });
      }

      let localOk = false;
      const localIds = new Set<string>();
      try {
        const local = await fetchLocalRooms();
        localOk = true;
        if (cancelled) return;
        for (const r of local.rooms) {
          localIds.add(r.id);
          const invite = parseInviteInput(r.inviteUrl);
          if (!invite) continue;
          const existingIdx = seeded.findIndex((s) => s.id === r.id);
          if (existingIdx >= 0) {
            seeded[existingIdx] = {
              ...seeded[existingIdx],
              title: r.title,
              token: invite.token,
              baseUrl: window.location.origin,
              role: "host",
              inviteUrl: r.inviteUrl,
            };
            continue;
          }
          seeded.push({
            id: r.id,
            title: r.title,
            baseUrl: window.location.origin,
            token: invite.token,
            role: "host",
            unread: 0,
            inviteUrl: r.inviteUrl,
          });
        }
      } catch {
        localOk = false;
      }

      if (!cancelled) setCanCreateLocal(localOk);

      // URL-seeded rooms on this origin that aren't in local manage list are guests
      for (const tab of seeded) {
        if (sameOriginBase(tab.baseUrl) && localIds.has(tab.id)) {
          tab.role = "host";
        } else if (tab.role === "host" && !localIds.has(tab.id)) {
          tab.role = "guest";
        }
      }

      for (const recent of loadRecentRooms()) {
        if (seeded.some((s) => s.id === recent.id && s.baseUrl === recent.baseUrl)) continue;
        const tab = recentToTab(recent);
        if (localIds.has(tab.id) && sameOriginBase(tab.baseUrl)) {
          tab.role = "host";
        }
        seeded.push(tab);
      }

      if (cancelled) return;
      if (seeded.length) {
        setRooms(seeded);
        const first = seeded[0];
        setActiveKey(roomKey(first));
        setSessions(Object.fromEntries(seeded.map((t) => [roomKey(t), emptySession()])));
        for (const t of seeded) saveRecentRoom(t);
      }
      setBootDone(true);
    })();

    return () => {
      cancelled = true;
    };
  }, []);

  useEffect(() => {
    return () => {
      for (const es of esMap.current.values()) es.close();
      esMap.current.clear();
    };
  }, []);

  useEffect(() => {
    if (!joined || !activeKey) return;
    const tab = rooms.find((r) => roomKey(r) === activeKey);
    if (!tab) return;
    void loadSession(tab);
  }, [joined, activeKey, rooms, loadSession]);

  const activeTab = rooms.find((r) => roomKey(r) === activeKey) ?? null;
  const activeSession = activeKey ? sessions[activeKey] ?? emptySession() : emptySession();

  const onJoinNick = (e: React.FormEvent) => {
    e.preventDefault();
    const name = nickname.trim();
    if (!name) {
      setShellError("请输入昵称");
      return;
    }
    localStorage.setItem(NICK_KEY, name);
    setShellError(null);
    setJoined(true);
  };

  const onSelect = (tab: RoomTab) => {
    setActiveKey(roomKey(tab));
    setRooms((prev) =>
      prev.map((r) => (roomKey(r) === roomKey(tab) ? { ...r, unread: 0 } : r)),
    );
    saveRecentRoom(tab);
  };

  const onCreate = async () => {
    try {
      setShellError(null);
      const created = await createLocalRoom();
      // Chat traffic can stay on current origin; manage used loopback.
      const baseUrl = window.location.origin;
      upsertTab(
        {
          id: created.id,
          title: created.title,
          baseUrl,
          token: created.token,
          role: "host",
          unread: 0,
          inviteUrl: created.inviteUrl,
        },
        true,
      );
    } catch (e) {
      setShellError(e instanceof Error ? e.message : "创建失败");
    }
  };

  const onJoinRemote = async () => {
    const parsed = parseInviteInput(joinValue);
    if (!parsed) {
      setJoinError("无法解析邀请链接，请粘贴完整 URL");
      return;
    }
    try {
      setJoinError(null);
      const meta = await fetchRoom(parsed.baseUrl, parsed.roomId, parsed.token);
      const role: RoomTab["role"] = sameOriginBase(parsed.baseUrl) ? "host" : "guest";
      upsertTab(
        {
          id: parsed.roomId,
          title: meta.title || "聊天房间",
          baseUrl: parsed.baseUrl,
          token: parsed.token,
          role,
          unread: 0,
          inviteUrl: `${parsed.baseUrl}/r/${parsed.roomId}?t=${encodeURIComponent(parsed.token)}`,
        },
        true,
      );
      setJoinOpen(false);
      setJoinValue("");
      if (!joined) {
        // still need nickname
      }
    } catch (e) {
      setJoinError(e instanceof Error ? e.message : "加入失败");
    }
  };

  const removeTab = (tab: RoomTab) => {
    const key = roomKey(tab);
    esMap.current.get(key)?.close();
    esMap.current.delete(key);
    removeRecentRoom(tab.id, tab.baseUrl);
    setSessions((prev) => {
      const next = { ...prev };
      delete next[key];
      return next;
    });
    setRooms((prev) => {
      const next = prev.filter((r) => roomKey(r) !== key);
      if (activeKeyRef.current === key) {
        setActiveKey(next[0] ? roomKey(next[0]) : null);
      }
      return next;
    });
  };

  const onLeave = (tab: RoomTab) => {
    removeTab(tab);
  };

  const onCloseHost = async (tab: RoomTab) => {
    try {
      if (tab.role === "host") {
        await closeLocalRoom(tab.id);
      }
    } catch (e) {
      setShellError(e instanceof Error ? e.message : "关闭失败");
      return;
    }
    removeTab(tab);
  };

  const onCopyInvite = async (tab: RoomTab) => {
    try {
      await navigator.clipboard.writeText(tab.inviteUrl);
    } catch {
      // ignore
    }
  };

  const onPickFiles = (files: FileList | null) => {
    if (!files?.length || !activeKey) return;
    const next: PendingFile[] = [];
    for (const file of Array.from(files)) {
      if (file.size > 25 * 1024 * 1024) {
        patchSession(activeKey, { error: `文件过大（上限 25MB）：${file.name}` });
        continue;
      }
      const id = crypto.randomUUID();
      const previewUrl = file.type.startsWith("image/") ? URL.createObjectURL(file) : undefined;
      next.push({ id, file, previewUrl });
    }
    setSessions((prev) => {
      const cur = prev[activeKey] ?? emptySession();
      return { ...prev, [activeKey]: { ...cur, pending: [...cur.pending, ...next] } };
    });
  };

  const onRemovePending = (id: string) => {
    if (!activeKey) return;
    setSessions((prev) => {
      const cur = prev[activeKey] ?? emptySession();
      const target = cur.pending.find((p) => p.id === id);
      if (target?.previewUrl) URL.revokeObjectURL(target.previewUrl);
      return {
        ...prev,
        [activeKey]: { ...cur, pending: cur.pending.filter((p) => p.id !== id) },
      };
    });
  };

  const onDraftChange = (v: string) => {
    if (!activeKey) return;
    patchSession(activeKey, { draft: v });
  };

  const onSend = async () => {
    if (!activeTab || !activeKey || sending) return;
    const session = sessionsRef.current[activeKey] ?? emptySession();
    const text = session.draft.trim();
    if (!text && session.pending.length === 0) return;

    setSending(true);
    patchSession(activeKey, { error: null });
    try {
      const attachmentIds: string[] = [];
      for (const p of session.pending) {
        const uploaded = await uploadAttachment(
          activeTab.baseUrl,
          activeTab.id,
          activeTab.token,
          p.file,
        );
        attachmentIds.push(uploaded.id);
        if (p.previewUrl) URL.revokeObjectURL(p.previewUrl);
      }
      const msg = await postMessage(activeTab.baseUrl, activeTab.id, activeTab.token, {
        senderId,
        displayName: nickname.trim(),
        text: text || undefined,
        attachmentIds: attachmentIds.length ? attachmentIds : undefined,
      });
      upsertMessage(activeKey, msg);
      patchSession(activeKey, { draft: "", pending: [] });
    } catch (e) {
      patchSession(activeKey, { error: e instanceof Error ? e.message : "发送失败" });
    } finally {
      setSending(false);
    }
  };

  if (!bootDone) {
    return (
      <div className="flex h-full items-center justify-center bg-[var(--room-bg)] text-sm text-[var(--text-muted)]">
        加载中…
      </div>
    );
  }

  if (!joined) {
    return (
      <div className="flex h-full items-center justify-center bg-[var(--room-bg)] p-6">
        <form
          onSubmit={onJoinNick}
          className="w-full max-w-sm rounded-2xl bg-[#1e1e1e] p-6 shadow-sm ring-1 ring-white/10"
        >
          <h1 className="text-center text-lg font-semibold text-[var(--text-primary)]">Athlon 聊天房间</h1>
          <p className="mt-1 text-center text-sm text-[var(--text-muted)]">输入昵称后进入多房间会话</p>
          <input
            className="mt-5 w-full rounded-xl border border-white/10 bg-[#2c2c2c] px-3 py-2.5 text-[var(--text-primary)] outline-none placeholder:text-[var(--text-muted)] focus:border-[#12b7f5]/60"
            placeholder="你的昵称"
            value={nickname}
            maxLength={32}
            autoFocus
            onChange={(e) => setNickname(e.target.value)}
          />
          {shellError ? <p className="mt-2 text-sm text-red-400">{shellError}</p> : null}
          <button
            type="submit"
            className="mt-4 w-full rounded-xl bg-[#12b7f5] py-2.5 font-medium text-white"
          >
            进入
          </button>
          <button
            type="button"
            className="mt-2 w-full rounded-xl border border-white/15 py-2.5 text-sm text-[var(--text-primary)] hover:bg-white/5"
            onClick={() => setJoinOpen(true)}
          >
            粘贴邀请链接加入
          </button>
        </form>
        <JoinRoomDialog
          open={joinOpen}
          value={joinValue}
          error={joinError}
          onChange={setJoinValue}
          onClose={() => {
            setJoinOpen(false);
            setJoinError(null);
          }}
          onSubmit={onJoinRemote}
        />
      </div>
    );
  }

  return (
    <div className="flex h-full bg-[var(--room-bg)]">
      <RoomSidebar
        rooms={rooms}
        activeKey={activeKey}
        collapsed={collapsed}
        onToggleCollapsed={() => setCollapsed((v) => !v)}
        onSelect={onSelect}
        onCreate={() => void onCreate()}
        onJoin={() => {
          setJoinError(null);
          setJoinOpen(true);
        }}
        onCopyInvite={(t) => void onCopyInvite(t)}
        onLeave={onLeave}
        onCloseHost={(t) => void onCloseHost(t)}
        canCreateLocal={canCreateLocal}
      />

      {activeTab ? (
        <ChatPane
          tab={activeTab}
          meta={activeSession.meta}
          nickname={nickname.trim()}
          messages={activeSession.messages}
          draft={activeSession.draft}
          pending={activeSession.pending}
          sending={sending}
          error={activeSession.error ?? shellError}
          senderId={senderId}
          onDraftChange={onDraftChange}
          onSend={() => void onSend()}
          onPickFiles={onPickFiles}
          onRemovePending={onRemovePending}
          onPreviewImage={(src, alt) => setLightbox({ src, alt })}
        />
      ) : (
        <div className="flex flex-1 flex-col items-center justify-center gap-3 p-6 text-center">
          <p className="text-sm text-[var(--text-muted)]">选择或创建一个房间开始聊天</p>
          {shellError ? <p className="text-sm text-red-400">{shellError}</p> : null}
          <div className="flex gap-2">
            {canCreateLocal ? (
              <button
                type="button"
                className="rounded-xl bg-[#12b7f5] px-4 py-2 text-sm font-medium text-white"
                onClick={() => void onCreate()}
              >
                新建房间
              </button>
            ) : null}
            <button
              type="button"
              className="rounded-xl border border-white/15 px-4 py-2 text-sm text-[var(--text-primary)]"
              onClick={() => setJoinOpen(true)}
            >
              加入房间
            </button>
          </div>
        </div>
      )}

      <JoinRoomDialog
        open={joinOpen}
        value={joinValue}
        error={joinError}
        onChange={setJoinValue}
        onClose={() => {
          setJoinOpen(false);
          setJoinError(null);
        }}
        onSubmit={onJoinRemote}
      />

      {lightbox ? (
        <Lightbox src={lightbox.src} alt={lightbox.alt} onClose={() => setLightbox(null)} />
      ) : null}
    </div>
  );
}
