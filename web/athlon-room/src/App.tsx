import { useCallback, useEffect, useRef, useState } from "react";
import {
  closeLocalRoom,
  connectEvents,
  createLocalRoom,
  fetchLocalRooms,
  fetchLocalSession,
  fetchMessages,
  fetchRoom,
  fetchRoomAgentProfile,
  fetchWorkspaces,
  newId,
  parseInviteInput,
  parseRoomFromLocation,
  postMessage,
  renameLocalRoom,
  resolveSenderId,
  saveRoomAgentProfile,
  setLocalRoomWorkspace,
  stopRoomAgent,
  uploadAttachment,
} from "./lib/api";
import { normalizeAgentRuntimeMode } from "./lib/runtimeMode";
import {
  loadRecentRooms,
  recentToTab,
  removeRecentRoom,
  saveRecentRoom,
} from "./lib/recentRooms";
import type {
  AgentTraceStep,
  PendingFile,
  RoomAgentCatalog,
  RoomAgentProfileData,
  RoomMessage,
  RoomMeta,
  RoomPresenceMember,
  RoomTab,
  WorkspaceItem,
} from "./types";
import { AthlonIcon } from "./components/AthlonIcon";
import { ChatPane } from "./components/ChatPane";
import { JoinRoomDialog } from "./components/JoinRoomDialog";
import { Lightbox } from "./components/Lightbox";
import { MentionToast, type MentionToastItem } from "./components/MentionToast";
import { RoomAgentSettingsDialog } from "./components/RoomAgentSettingsDialog";
import { RoomSettingsDialog } from "./components/RoomSettingsDialog";
import { RoomSidebar, roomKey } from "./components/RoomSidebar";
import {
  messageMentionsSender,
  previewMentionBody,
  resolveMentionedSenderIds,
} from "./lib/mentions";

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
  /** Host-only live traces keyed by agent message id. */
  agentTraces: Record<string, AgentTraceStep[]>;
  members: RoomPresenceMember[];
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
    agentTraces: {},
    members: [],
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
  const [senderId, setSenderId] = useState("");
  const [nickname, setNickname] = useState(() => localStorage.getItem(NICK_KEY) ?? "");
  const [joined, setJoined] = useState(false);
  const [rooms, setRooms] = useState<RoomTab[]>([]);
  const [activeKey, setActiveKey] = useState<string | null>(null);
  const [sessions, setSessions] = useState<Record<string, SessionBundle>>({});
  const [sending, setSending] = useState(false);
  const [stoppingAgent, setStoppingAgent] = useState(false);
  const [joinOpen, setJoinOpen] = useState(false);
  const [joinValue, setJoinValue] = useState("");
  const [joinError, setJoinError] = useState<string | null>(null);
  const [shellError, setShellError] = useState<string | null>(null);
  const [lightbox, setLightbox] = useState<{ src: string; alt: string } | null>(null);
  const [canCreateLocal, setCanCreateLocal] = useState(false);
  const [bootDone, setBootDone] = useState(false);
  /** Room created via「新建」and not yet confirmed in settings — cancel discards it. */
  const [draftRoomKey, setDraftRoomKey] = useState<string | null>(null);
  const [mentionToast, setMentionToast] = useState<MentionToastItem | null>(null);

  const [settingsTab, setSettingsTab] = useState<RoomTab | null>(null);
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [settingsSaving, setSettingsSaving] = useState(false);
  const [settingsError, setSettingsError] = useState<string | null>(null);
  const [workspaces, setWorkspaces] = useState<WorkspaceItem[]>([]);

  const [agentTab, setAgentTab] = useState<RoomTab | null>(null);
  const [agentOpen, setAgentOpen] = useState(false);
  const [agentSaving, setAgentSaving] = useState(false);
  const [agentError, setAgentError] = useState<string | null>(null);
  const [agentProfile, setAgentProfile] = useState<RoomAgentProfileData | null>(null);
  const [agentCatalog, setAgentCatalog] = useState<RoomAgentCatalog | null>(null);

  const roomsRef = useRef(rooms);
  const activeKeyRef = useRef(activeKey);
  const nicknameRef = useRef(nickname);
  const senderIdRef = useRef(senderId);
  const esMap = useRef(new Map<string, EventSource>());
  const sessionsRef = useRef(sessions);
  const notifiedMentionIds = useRef(new Set<string>());

  useEffect(() => {
    roomsRef.current = rooms;
  }, [rooms]);
  useEffect(() => {
    activeKeyRef.current = activeKey;
  }, [activeKey]);
  useEffect(() => {
    nicknameRef.current = nickname;
  }, [nickname]);
  useEffect(() => {
    senderIdRef.current = senderId;
  }, [senderId]);
  useEffect(() => {
    sessionsRef.current = sessions;
  }, [sessions]);

  useEffect(() => {
    if (!joined) return;
    if (typeof Notification === "undefined") return;
    if (Notification.permission !== "default") return;
    void Notification.requestPermission().catch(() => undefined);
  }, [joined]);

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

  const applyRoomTitle = useCallback((key: string, title: string) => {
    const next = title.trim();
    if (!next) return;
    const current = roomsRef.current.find((r) => roomKey(r) === key);
    const metaTitle = sessionsRef.current[key]?.meta?.title;
    if (current?.title === next && metaTitle === next) return;

    setRooms((prev) =>
      prev.map((r) => (roomKey(r) === key ? { ...r, title: next } : r)),
    );
    setSessions((prev) => {
      const cur = prev[key];
      if (!cur?.meta || cur.meta.title === next) return prev;
      return {
        ...prev,
        [key]: { ...cur, meta: { ...cur.meta, title: next } },
      };
    });
    if (current && current.title !== next) {
      saveRecentRoom({ ...current, title: next });
    }
  }, []);

  const notifyMention = useCallback((key: string, msg: RoomMessage) => {
    const me = senderIdRef.current;
    if (!me || msg.senderId === me) return;
    if (!messageMentionsSender(msg, me, nicknameRef.current)) return;
    if (notifiedMentionIds.current.has(msg.id)) return;
    notifiedMentionIds.current.add(msg.id);

    const tab = roomsRef.current.find((r) => roomKey(r) === key);
    const roomTitle = tab?.title || "Athlon Teams";
    const fromName = msg.displayName?.trim() || "队友";
    const body = previewMentionBody(msg.text);
    const toastId = msg.id;

    setMentionToast({
      id: toastId,
      roomKey: key,
      roomTitle,
      fromName,
      body,
    });

    if (
      typeof Notification !== "undefined" &&
      Notification.permission === "granted" &&
      (document.hidden || activeKeyRef.current !== key)
    ) {
      try {
        const note = new Notification(`${fromName} @了你 · ${roomTitle}`, {
          body,
          tag: `room-mention-${msg.id}`,
        });
        note.onclick = () => {
          window.focus();
          setActiveKey(key);
          setRooms((prev) =>
            prev.map((r) => (roomKey(r) === key ? { ...r, unread: 0 } : r)),
          );
          note.close();
        };
      } catch {
        // Ignore browsers that reject Notification construction.
      }
    }
  }, []);

  const upsertMessage = useCallback(
    (key: string, msg: RoomMessage) => {
      let isNew = false;
      setSessions((prev) => {
        const cur = prev[key] ?? emptySession();
        if (cur.seenIds.has(msg.id)) {
          const index = cur.messages.findIndex((m) => m.id === msg.id);
          if (index < 0) return prev;
          const messages = [...cur.messages];
          messages[index] = { ...messages[index], ...msg };
          return { ...prev, [key]: { ...cur, messages } };
        }
        isNew = true;
        const seenIds = new Set(cur.seenIds);
        seenIds.add(msg.id);
        const messages = [...cur.messages, msg].sort((a, b) =>
          a.createdAt.localeCompare(b.createdAt),
        );
        return { ...prev, [key]: { ...cur, messages, seenIds } };
      });
      if (isNew) {
        bumpUnread(key);
        notifyMention(key, msg);
      }
    },
    [bumpUnread, notifyMention],
  );

  const appendAgentTrace = useCallback((key: string, step: AgentTraceStep) => {
    if (!step.messageId) return;
    setSessions((prev) => {
      const cur = prev[key] ?? emptySession();
      const list = cur.agentTraces[step.messageId] ?? [];
      if (list.some((s) => s.id === step.id)) return prev;
      return {
        ...prev,
        [key]: {
          ...cur,
          agentTraces: {
            ...cur.agentTraces,
            [step.messageId]: [...list, step],
          },
        },
      };
    });
  }, []);

  const ensureSse = useCallback(
    (tab: RoomTab) => {
      const key = roomKey(tab);
      if (esMap.current.has(key)) return;
      if (!senderId) return;

      while (esMap.current.size >= MAX_SSE) {
        const victim = [...esMap.current.keys()].find((k) => k !== activeKeyRef.current);
        if (!victim) break;
        esMap.current.get(victim)?.close();
        esMap.current.delete(victim);
      }

      const es = connectEvents(
        tab.baseUrl,
        tab.id,
        tab.token,
        {
          senderId,
          displayName: nicknameRef.current.trim() || "访客",
        },
        (msg) => upsertMessage(key, msg),
        (msg) => upsertMessage(key, msg),
        // Server only emits agent_trace on loopback; guests simply never receive it.
        (step) => appendAgentTrace(key, step),
        (snapshot) => {
          patchSession(key, { members: snapshot.members ?? [] });
          if (snapshot.title) applyRoomTitle(key, snapshot.title);
        },
      );
      esMap.current.set(key, es);
    },
    [appendAgentTrace, applyRoomTitle, patchSession, senderId, upsertMessage],
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
          prev.map((r) =>
            roomKey(r) === key
              ? {
                  ...r,
                  title: meta.title || r.title,
                  workspaceId: meta.workspaceId ?? r.workspaceId,
                  workspaceRoot: meta.workspaceRoot ?? r.workspaceRoot,
                  workspaceName: meta.workspaceName ?? r.workspaceName,
                  agentRuntimeMode: meta.agentRuntimeMode ?? r.agentRuntimeMode,
                }
              : r,
          ),
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

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const id = await resolveSenderId(window.location.origin);
        if (!cancelled) setSenderId(id);
      } catch {
        // keep empty until retry on join/send
      }

      const seeded: RoomTab[] = [];
      const fromUrl = parseRoomFromLocation();
      if (fromUrl) {
        let title = "Athlon Teams";
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
        for (const r of local.rooms as Array<{
          id: string;
          title: string;
          inviteUrl: string;
          workspaceRoot?: string;
          workspaceId?: string;
          workspaceName?: string;
          agentRuntimeMode?: string;
        }>) {
          localIds.add(r.id);
          const invite = parseInviteInput(r.inviteUrl);
          if (!invite) continue;
          const existingIdx = seeded.findIndex((s) => s.id === r.id);
          const patch: RoomTab = {
            id: r.id,
            title: r.title,
            baseUrl: window.location.origin,
            token: invite.token,
            role: "host",
            unread: 0,
            inviteUrl: r.inviteUrl,
            workspaceRoot: r.workspaceRoot,
            workspaceId: r.workspaceId,
            workspaceName: r.workspaceName,
            agentRuntimeMode: normalizeAgentRuntimeMode(r.agentRuntimeMode),
          };
          if (existingIdx >= 0) {
            seeded[existingIdx] = { ...seeded[existingIdx], ...patch };
            continue;
          }
          seeded.push(patch);
        }
      } catch {
        localOk = false;
      }

      if (!cancelled) setCanCreateLocal(localOk);

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
        // Same-origin recent rooms not in host ActiveRoomIds are stale (chat may work via
        // disk token but @agent won't). Prefer active host rooms / invite links only.
        if (localOk && sameOriginBase(tab.baseUrl) && !localIds.has(tab.id)) {
          continue;
        }
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

      let ssoName = "";
      try {
        const me = await fetchLocalSession();
        ssoName = me.displayName?.trim() ?? "";
      } catch {
        ssoName = "";
      }
      if (cancelled) return;
      if (ssoName) {
        const name = ssoName.slice(0, 32);
        localStorage.setItem(NICK_KEY, name);
        setNickname(name);
        setJoined(true);
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
    if (!joined || !activeKey || !senderId) return;
    const tab = rooms.find((r) => roomKey(r) === activeKey);
    if (!tab) return;
    void loadSession(tab);
  }, [joined, activeKey, rooms, loadSession, senderId]);

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
      setCanCreateLocal(true);
      const created = await createLocalRoom();
      const baseUrl = window.location.origin;
      const tab: RoomTab = {
        id: created.id,
        title: created.title,
        baseUrl,
        token: created.token,
        role: "host",
        unread: 0,
        inviteUrl: created.inviteUrl,
        workspaceRoot: null,
        workspaceId: null,
        workspaceName: null,
        agentRuntimeMode: "local",
      };
      upsertTab(tab, true);
      setDraftRoomKey(roomKey(tab));
      // Require host to set workspace manually — no silent default.
      void openRoomSettings(tab);
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
      const [meta, id] = await Promise.all([
        fetchRoom(parsed.baseUrl, parsed.roomId, parsed.token),
        resolveSenderId(parsed.baseUrl),
      ]);
      setSenderId(id);
      const role: RoomTab["role"] = sameOriginBase(parsed.baseUrl) ? "host" : "guest";
      upsertTab(
        {
          id: parsed.roomId,
          title: meta.title || "Athlon Teams",
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
    } catch (e) {
      setJoinError(e instanceof Error ? e.message : "加入失败");
    }
  };

  const removeTab = (tab: RoomTab) => {
    const key = roomKey(tab);
    esMap.current.get(key)?.close();
    esMap.current.delete(key);
    removeRecentRoom(tab.id, tab.baseUrl);
    setDraftRoomKey((prev) => (prev === key ? null : prev));
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

  const openRoomSettings = async (tab: RoomTab) => {
    setSettingsTab(tab);
    setSettingsError(null);
    setSettingsOpen(true);
    try {
      const list = await fetchWorkspaces();
      setWorkspaces(list);
    } catch {
      setWorkspaces([]);
    }
  };

  const onSaveRoomSettings = async (input: {
    title: string;
    workspaceId: string | null;
    workspaceRoot: string | null;
    agentRuntimeMode: "local" | "sandbox" | "cloud_sandbox";
  }) => {
    if (!settingsTab) return;
    setSettingsSaving(true);
    setSettingsError(null);
    try {
      const renamed = await renameLocalRoom(settingsTab.id, input.title);
      const ws = await setLocalRoomWorkspace(settingsTab.id, {
        workspaceId: input.workspaceId,
        workspaceRoot: input.workspaceRoot,
        agentRuntimeMode: input.agentRuntimeMode,
      });
      const key = roomKey(settingsTab);
      const mode = normalizeAgentRuntimeMode(ws.agentRuntimeMode ?? input.agentRuntimeMode);
      setRooms((prev) =>
        prev.map((r) =>
          roomKey(r) === key
            ? {
                ...r,
                title: renamed.title,
                workspaceId: ws.workspaceId,
                workspaceRoot: ws.workspaceRoot,
                workspaceName: ws.workspaceName,
                agentRuntimeMode: mode,
              }
            : r,
        ),
      );
      setSessions((prev) => {
        const cur = prev[key];
        if (!cur?.meta) return prev;
        return {
          ...prev,
          [key]: {
            ...cur,
            meta: {
              ...cur.meta,
              title: renamed.title,
              workspaceId: ws.workspaceId,
              workspaceRoot: ws.workspaceRoot,
              workspaceName: ws.workspaceName,
              agentRuntimeMode: mode,
            },
          },
        };
      });
      setDraftRoomKey((prev) => (prev === key ? null : prev));
      setSettingsOpen(false);
      setSettingsTab(null);
    } catch (e) {
      setSettingsError(e instanceof Error ? e.message : "保存失败");
    } finally {
      setSettingsSaving(false);
    }
  };

  const discardDraftRoomIfNeeded = async () => {
    const tab = settingsTab;
    const key = tab ? roomKey(tab) : null;
    setSettingsOpen(false);
    setSettingsTab(null);
    setSettingsError(null);
    if (!tab || !key || draftRoomKey !== key) {
      return;
    }

    // 「新建房间」后取消设置 → 丢弃尚未确认的房间，避免空房间继续生效。
    setDraftRoomKey(null);
    try {
      if (tab.role === "host") {
        await closeLocalRoom(tab.id);
      }
    } catch {
      // still remove from UI
    }
    removeTab(tab);
  };

  const openAgentCaps = async (tab: RoomTab) => {
    setAgentTab(tab);
    setAgentError(null);
    setAgentOpen(true);
    try {
      const data = await fetchRoomAgentProfile(tab.id);
      setAgentProfile(data.profile);
      setAgentCatalog(data.catalog);
    } catch (e) {
      setAgentError(e instanceof Error ? e.message : "无法加载 Agent 能力");
      setAgentProfile(null);
      setAgentCatalog(null);
    }
  };

  const onSaveAgentCaps = async (profile: RoomAgentProfileData) => {
    if (!agentTab) return;
    setAgentSaving(true);
    setAgentError(null);
    try {
      const data = await saveRoomAgentProfile(agentTab.id, profile);
      setAgentProfile(data.profile);
      setAgentCatalog(data.catalog);
      setAgentOpen(false);
      setAgentTab(null);
    } catch (e) {
      setAgentError(e instanceof Error ? e.message : "保存失败");
    } finally {
      setAgentSaving(false);
    }
  };

  const onPickFiles = (files: FileList | File[] | null) => {
    if (!files || !activeKey) return;
    const list = Array.isArray(files) ? files : Array.from(files);
    if (list.length === 0) return;
    const next: PendingFile[] = [];
    for (const file of list) {
      const id = newId();
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

  const onStopAgent = async () => {
    if (!activeTab || activeTab.role !== "host" || stoppingAgent) return;
    setStoppingAgent(true);
    try {
      await stopRoomAgent(activeTab.id);
    } catch (e) {
      setShellError(e instanceof Error ? e.message : String(e));
    } finally {
      setStoppingAgent(false);
    }
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
      const mentionedSenderIds = text
        ? resolveMentionedSenderIds(text, session.members)
        : [];
      const msg = await postMessage(activeTab.baseUrl, activeTab.id, activeTab.token, {
        senderId,
        displayName: nickname.trim(),
        text: text || undefined,
        attachmentIds: attachmentIds.length ? attachmentIds : undefined,
        mentionedSenderIds: mentionedSenderIds.length ? mentionedSenderIds : undefined,
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
      <div className="flex h-full items-center justify-center bg-[#f1f5f9] text-sm text-slate-500 dark:bg-[#0f0f10] dark:text-zinc-400">
        加载中…
      </div>
    );
  }

  if (!joined) {
    return (
      <div className="flex h-full items-center justify-center bg-[#f1f5f9] p-6 dark:bg-[#0f0f10]">
        <form
          onSubmit={onJoinNick}
          className="w-full max-w-sm rounded-3xl border border-slate-200 bg-white p-6 shadow-xl shadow-sky-100/70 dark:border-zinc-800 dark:bg-[#1f1f22] dark:shadow-none"
        >
          <div className="mb-1 flex justify-center">
            <AthlonIcon className="h-12 w-12 rounded-2xl shadow-sm shadow-sky-100" />
          </div>
          <h1 className="text-center text-lg font-semibold text-slate-900 dark:text-zinc-100">
            Athlon Teams
          </h1>
          <p className="mt-1 text-center text-sm text-slate-500 dark:text-zinc-400">
            输入昵称后进入多房间会话
          </p>
          <input
            className="mt-5 w-full rounded-2xl border border-slate-200 bg-slate-50 px-3 py-2.5 text-slate-900 outline-none placeholder:text-slate-400 focus:border-sky-300 focus:ring-2 focus:ring-sky-100 dark:border-zinc-700 dark:bg-zinc-800 dark:text-zinc-100 dark:placeholder:text-zinc-500"
            placeholder="你的昵称"
            value={nickname}
            maxLength={32}
            autoFocus
            onChange={(e) => setNickname(e.target.value)}
          />
          {shellError ? <p className="mt-2 text-sm text-red-500">{shellError}</p> : null}
          <button
            type="submit"
            className="mt-4 w-full rounded-2xl bg-sky-600 py-2.5 font-medium text-white transition hover:bg-sky-700"
          >
            进入
          </button>
          <button
            type="button"
            className="mt-2 w-full rounded-2xl border border-slate-200 py-2.5 text-sm text-slate-700 transition hover:bg-slate-50 dark:border-zinc-700 dark:text-zinc-200 dark:hover:bg-zinc-800"
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
          onSubmit={() => void onJoinRemote()}
        />
      </div>
    );
  }

  return (
    <div className="flex h-full bg-[#f1f5f9] dark:bg-[#0f0f10]">
      <RoomSidebar
        rooms={rooms}
        activeKey={activeKey}
        onSelect={onSelect}
        onCreate={() => void onCreate()}
        onJoin={() => {
          setJoinError(null);
          setJoinOpen(true);
        }}
        onCopyInvite={(t) => void onCopyInvite(t)}
        onLeave={onLeave}
        onCloseHost={(t) => void onCloseHost(t)}
        onOpenSettings={(t) => void openRoomSettings(t)}
        onOpenAgentCaps={(t) => void openAgentCaps(t)}
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
          members={activeSession.members}
          agentTraces={activeSession.agentTraces}
          stoppingAgent={stoppingAgent}
          onDraftChange={onDraftChange}
          onSend={() => void onSend()}
          onPickFiles={onPickFiles}
          onRemovePending={onRemovePending}
          onPreviewImage={(src, alt) => setLightbox({ src, alt })}
          onOpenSettings={
            activeTab.role === "host" ? () => void openRoomSettings(activeTab) : undefined
          }
          onStopAgent={
            activeTab.role === "host" ? () => void onStopAgent() : undefined
          }
        />
      ) : (
        <div className="flex flex-1 flex-col items-center justify-center gap-3 p-6 text-center">
          <p className="text-sm text-slate-500 dark:text-zinc-400">选择或创建一个团队房间开始协作</p>
          {shellError ? <p className="text-sm text-red-500">{shellError}</p> : null}
          <div className="flex gap-2">
            {canCreateLocal ? (
              <button
                type="button"
                className="rounded-2xl bg-sky-600 px-4 py-2 text-sm font-medium text-white hover:bg-sky-700"
                onClick={() => void onCreate()}
              >
                新建房间
              </button>
            ) : null}
            <button
              type="button"
              className="rounded-2xl border border-slate-200 px-4 py-2 text-sm text-slate-700 hover:bg-white dark:border-zinc-700 dark:text-zinc-200 dark:hover:bg-zinc-800"
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
        onSubmit={() => void onJoinRemote()}
      />

      <RoomSettingsDialog
        open={settingsOpen}
        tab={settingsTab}
        workspaces={workspaces}
        saving={settingsSaving}
        error={settingsError}
        onClose={() => void discardDraftRoomIfNeeded()}
        onSave={(input) => void onSaveRoomSettings(input)}
        onOpenAgentCaps={
          settingsTab
            ? () => {
                const tab = settingsTab;
                // Opening agent caps confirms the draft room (do not discard).
                setDraftRoomKey((prev) =>
                  prev && tab && roomKey(tab) === prev ? null : prev,
                );
                setSettingsOpen(false);
                void openAgentCaps(tab);
              }
            : undefined
        }
      />

      <RoomAgentSettingsDialog
        open={agentOpen}
        profile={agentProfile}
        catalog={agentCatalog}
        saving={agentSaving}
        error={agentError}
        onClose={() => {
          setAgentOpen(false);
          setAgentTab(null);
          setAgentError(null);
        }}
        onSave={(profile) => void onSaveAgentCaps(profile)}
      />

      {lightbox ? (
        <Lightbox src={lightbox.src} alt={lightbox.alt} onClose={() => setLightbox(null)} />
      ) : null}

      <MentionToast
        item={mentionToast}
        onClose={() => setMentionToast(null)}
        onOpen={() => {
          if (!mentionToast) return;
          const key = mentionToast.roomKey;
          setActiveKey(key);
          setRooms((prev) =>
            prev.map((r) => (roomKey(r) === key ? { ...r, unread: 0 } : r)),
          );
          setMentionToast(null);
        }}
      />
    </div>
  );
}
