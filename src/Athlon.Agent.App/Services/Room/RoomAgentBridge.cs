using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Browser;
using Athlon.Agent.Core.Debug;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.Knowledge;
using Athlon.Agent.Core.Plan;
using Athlon.Agent.Core.Prompt;
using Athlon.Agent.Core.RuntimeDiagnostics;
using Athlon.Agent.Core.Streaming;
using Athlon.Agent.Core.Teams;
using Athlon.Agent.Core.Terminal;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Infrastructure.Teams;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Athlon.Agent.App.Services.Room;

public sealed class RoomAgentBridge : IAsyncDisposable
{
    private static readonly Regex MentionRegex = new(
        @"@(?:agent|athlon)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly RoomStore _store;
    private readonly AthlonRoomServer _server;
    private readonly RoomAgentProfileStore _profileStore;
    private readonly RoomAgentSessionStore _sessionStore;
    private readonly IAgentRuntime _runtime;
    private readonly IServiceProvider _services;
    private readonly IActiveWorkspaceContext _workspaceContext;
    private readonly AppSettings _settings;
    private readonly ILogger<RoomAgentBridge> _logger;
    private readonly ConcurrentDictionary<string, byte> _attachedRooms = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _roomGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _runCtsByRoom =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IDisposable> _subscriptions = new(StringComparer.OrdinalIgnoreCase);

    public RoomAgentBridge(
        RoomStore store,
        AthlonRoomServer server,
        RoomAgentProfileStore profileStore,
        RoomAgentSessionStore sessionStore,
        IAgentRuntime runtime,
        IServiceProvider services,
        IActiveWorkspaceContext workspaceContext,
        AppSettings settings,
        ILogger<RoomAgentBridge> logger)
    {
        _store = store;
        _server = server;
        _profileStore = profileStore;
        _sessionStore = sessionStore;
        _runtime = runtime;
        _services = services;
        _workspaceContext = workspaceContext;
        _settings = settings;
        _logger = logger;
        _server.SetRoomLifecycleHooks(AttachRoom, DetachRoom);
        _server.SetAgentStopHandler(TryStop);
    }

    /// <summary>Cancel the in-flight @agent turn for a room (host-only API). Returns false if idle.</summary>
    public bool TryStop(string roomId)
    {
        if (string.IsNullOrWhiteSpace(roomId))
        {
            return false;
        }

        if (!_runCtsByRoom.TryGetValue(roomId, out var cts))
        {
            return false;
        }

        try
        {
            if (cts.IsCancellationRequested)
            {
                return true;
            }

            cts.Cancel();
            _logger.LogInformation("Room agent stop requested for {RoomId}", roomId);
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    public void AttachToActiveRoom()
    {
        foreach (var roomId in _server.ActiveRoomIds)
        {
            AttachRoom(roomId);
        }
    }

    public void AttachRoom(string roomId)
    {
        if (string.IsNullOrWhiteSpace(roomId))
        {
            return;
        }

        if (!_attachedRooms.TryAdd(roomId, 0))
        {
            return;
        }

        var sub = _store.SubscribeEvents(roomId, ev =>
        {
            if (ev.Kind != RoomStoreEventKind.Message)
            {
                return;
            }

            _ = Task.Run(() => HandleMessageAsync(roomId, ev.Message));
        });
        _subscriptions[roomId] = sub;
        _logger.LogInformation("RoomAgentBridge attached to room {RoomId}", roomId);
    }

    public void DetachRoom(string roomId)
    {
        if (_subscriptions.TryRemove(roomId, out var sub))
        {
            sub.Dispose();
        }

        _attachedRooms.TryRemove(roomId, out _);
        _logger.LogInformation("RoomAgentBridge detached from room {RoomId}", roomId);
    }

    public void DetachAll()
    {
        foreach (var key in _subscriptions.Keys.ToArray())
        {
            DetachRoom(key);
        }
    }

    public ValueTask DisposeAsync()
    {
        DetachAll();
        foreach (var gate in _roomGates.Values)
        {
            gate.Dispose();
        }

        _roomGates.Clear();
        return ValueTask.CompletedTask;
    }

    private async Task HandleMessageAsync(string roomId, RoomMessage message)
    {
        if (string.Equals(message.SenderId, RoomAgentIds.SenderId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(message.Text) || !MentionRegex.IsMatch(message.Text))
        {
            return;
        }

        var taskText = MentionRegex.Replace(message.Text, string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(taskText) && message.Attachments.Count == 0)
        {
            _store.AppendMessage(roomId, new RoomMessage
            {
                SenderId = RoomAgentIds.SenderId,
                DisplayName = RoomAgentIds.DisplayName,
                Kind = "agent",
                Text = "请在 @agent 后面写上你的问题或任务。",
                CreatedAt = DateTimeOffset.UtcNow
            });
            return;
        }

        var gate = _roomGates.GetOrAdd(roomId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        var cts = new CancellationTokenSource();
        _runCtsByRoom[roomId] = cts;
        try
        {
            await RunAgentTurnAsync(roomId, message, taskText, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Room agent turn cancelled for {RoomId}", roomId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Room agent turn failed for {RoomId}", roomId);
            try
            {
                _store.AppendMessage(roomId, new RoomMessage
                {
                    SenderId = RoomAgentIds.SenderId,
                    DisplayName = RoomAgentIds.DisplayName,
                    Kind = "agent",
                    Text = "抱歉，处理失败：" + ex.Message,
                    CreatedAt = DateTimeOffset.UtcNow
                });
            }
            catch
            {
                // ignore
            }
        }
        finally
        {
            _runCtsByRoom.TryRemove(roomId, out _);
            cts.Dispose();
            gate.Release();
        }
    }

    private async Task RunAgentTurnAsync(
        string roomId,
        RoomMessage trigger,
        string taskText,
        CancellationToken cancellationToken)
    {
        var profile = _profileStore.GetOrCreate(roomId);
        var placeholder = _store.AppendMessage(roomId, new RoomMessage
        {
            SenderId = RoomAgentIds.SenderId,
            DisplayName = RoomAgentIds.DisplayName,
            Kind = "agent",
            Text = "…",
            CreatedAt = DateTimeOffset.UtcNow
        });

        var prompt = BuildUserPrompt(trigger, taskText);

        var room = _store.GetRoom(roomId);
        var (workspaceRoot, workspaceId) = ResolveRoomWorkspace(room);
        // Persist across @agent mentions so CompactionTurnMiddleware can compact like Chat.
        var session = await _sessionStore
            .LoadOrCreateAsync(roomId, $"Room {roomId}", cancellationToken)
            .ConfigureAwait(false);
        session = session.WithWorkspace(workspaceRoot, workspaceId);
        if (!string.IsNullOrWhiteSpace(profile.ModelNameOverride))
        {
            session = session with { ModelName = profile.ModelNameOverride };
        }

        // Mirror all room chatter into the agent transcript (agent bubbles skipped — already from SendAsync).
        var syncState = await _sessionStore.LoadSyncStateAsync(roomId, cancellationToken).ConfigureAwait(false);
        session = SyncRoomChatIntoSession(session, syncState, roomId, excludeMessageId: trigger.Id);
        await _sessionStore.SaveAsync(roomId, session, cancellationToken).ConfigureAwait(false);
        await _sessionStore.SaveSyncStateAsync(roomId, syncState, cancellationToken).ConfigureAwait(false);

        // Selecting Skills in room settings auto-attaches load_skill_through_path.
        var allowList = RoomAgentProfileStore.ResolveToolAllowList(profile);

        var localTools = _services.GetServices<IAgentTool>();
        var mcpRegistry = _services.GetRequiredService<IMcpRegistry>();
        var activeSessionContext = _services.GetRequiredService<IActiveAgentSessionContext>();
        var sessionKnowledgeState = _services.GetRequiredService<ISessionKnowledgeState>();
        var sessionHarnessState = _services.GetRequiredService<ISessionHarnessState>();
        var runContextAccessor = _services.GetRequiredService<IAgentRunContextAccessor>();
        var debugPhaseAccessor = _services.GetRequiredService<IDebugPhaseAccessor>();
        var planPhaseAccessor = _services.GetRequiredService<IPlanPhaseAccessor>();
        var workspaceGuard = _services.GetRequiredService<WorkspaceGuard>();
        var browserWorkspaceState = _services.GetRequiredService<IBrowserWorkspaceState>();
        var terminalWorkspaceState = _services.GetRequiredService<ITerminalWorkspaceState>();
        var promptOrchestrator = _services.GetRequiredService<ISystemPromptOrchestrator>();
        var logger = _services.GetRequiredService<IAppLogger>();

        var router = new ProfileToolRouter(
            allowList,
            isLeader: false,
            localTools,
            mcpRegistry,
            _settings,
            activeSessionContext,
            sessionKnowledgeState,
            sessionHarnessState,
            runContextAccessor,
            debugPhaseAccessor,
            planPhaseAccessor,
            workspaceGuard,
            browserWorkspaceState,
            terminalWorkspaceState,
            runtimeDiagnosticEventSink: _services.GetService<IRuntimeDiagnosticEventSink>(),
            logger: logger);

        var ignorePatterns = WorkspaceSessionResolver.ResolveIgnorePatterns(session, _settings);
        var workspaceKind = WorkspaceSessionResolver.ResolveKind(session, _settings);
        var parent = AgentRunContext.CreateRoot(
            session,
            IdGen.NewId(),
            router,
            promptOrchestrator,
            ignorePatterns,
            workspaceKind);

        var completePrompt = BuildSystemPrompt(profile, workspaceRoot);
        var member = parent.CreateTeamMember(
            session.Id,
            teamTaskId: "room-" + roomId,
            profileId: profile.Id,
            memberRouter: router,
            memberPrompt: promptOrchestrator,
            role: profile.Name,
            loopOptions: null,
            workspaceRoot: workspaceRoot,
            ignorePatterns: ignorePatterns,
            completeSystemPrompt: completePrompt,
            teamId: "room",
            isLeader: false);

        var profileOptions = new AgentProfileTurnOptions(
            ProfileId: profile.Id,
            ModelNameOverride: profile.ModelNameOverride,
            MaxModelToolRounds: profile.MaxModelToolRounds,
            SkillNames: profile.SkillNames,
            McpServerNames: profile.McpServerNames,
            ToolAllowList: allowList,
            KnowledgeModuleIds: profile.KnowledgeModuleIds,
            IsLeader: false,
            TeamTaskId: "room-" + roomId,
            RootSessionId: session.Id);

        var streamed = new StringBuilder();
        var lastFlush = 0L;
        var messageId = placeholder.Id;
        var toolNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var toolArgs = new Dictionary<string, StringBuilder>(StringComparer.OrdinalIgnoreCase);
        var lastArgsPublish = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var reasoning = new StringBuilder();

        void PublishTrace(string type, string? toolCallId = null, string? toolName = null, string? summary = null)
        {
            try
            {
                _store.PublishTrace(roomId, new RoomAgentTraceEvent
                {
                    MessageId = messageId,
                    Type = type,
                    ToolCallId = toolCallId,
                    ToolName = toolName,
                    Summary = TruncateTrace(summary)
                });
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to publish room agent trace.");
            }
        }

        void FlushText(bool force)
        {
            var now = Environment.TickCount64;
            if (!force && now - lastFlush < 120)
            {
                return;
            }

            lastFlush = now;
            _store.UpdateMessageText(roomId, messageId, streamed.ToString());
        }

        PublishTrace("run_started", summary: "开始处理");

        using var roomScope = RoomAgentTurnScope.Enter(new RoomAgentTurnOptions(roomId));
        using var profileScope = AgentProfileTurnScope.Enter(profileOptions);
        using var runScope = runContextAccessor.Push(member);

        var lastPersistMs = 0L;
        async Task PersistSessionAsync(AgentSession updated, CancellationToken ct)
        {
            session = updated;
            try
            {
                await _sessionStore.SaveAsync(roomId, session, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to persist room agent session for {RoomId}", roomId);
            }
        }

        try
        {
            var result = await _runtime.SendAsync(
                session,
                prompt,
                callbacks: new AgentTurnCallbacks
                {
                    OnSessionUpdated = async updated =>
                    {
                        var now = Environment.TickCount64;
                        if (now - lastPersistMs < 400)
                        {
                            session = updated;
                            return;
                        }

                        lastPersistMs = now;
                        await PersistSessionAsync(updated, CancellationToken.None).ConfigureAwait(false);
                    },
                    OnToolApprovalRequested = static (_, _) =>
                        Task.FromResult(ToolApprovalDecision.Approved),
                    OnStreamEvent = ev =>
                    {
                        switch (ev)
                        {
                            case AgentStreamEvent.TextMessageContent content
                                when !string.IsNullOrEmpty(content.Delta):
                                streamed.Append(content.Delta);
                                FlushText(force: false);
                                break;

                            case AgentStreamEvent.ToolCallStart start:
                                toolNames[start.ToolCallId] = start.ToolName;
                                toolArgs[start.ToolCallId] = new StringBuilder();
                                PublishTrace("tool_start", start.ToolCallId, start.ToolName, $"调用 {start.ToolName}");
                                break;

                            case AgentStreamEvent.ToolCallArgs args:
                            {
                                if (!toolArgs.TryGetValue(args.ToolCallId, out var buf))
                                {
                                    buf = new StringBuilder();
                                    toolArgs[args.ToolCallId] = buf;
                                }

                                buf.Append(args.Delta);
                                var now = Environment.TickCount64;
                                if (!lastArgsPublish.TryGetValue(args.ToolCallId, out var last) || now - last >= 400)
                                {
                                    lastArgsPublish[args.ToolCallId] = now;
                                    toolNames.TryGetValue(args.ToolCallId, out var name);
                                    PublishTrace("tool_args", args.ToolCallId, name, buf.ToString());
                                }

                                break;
                            }

                            case AgentStreamEvent.ToolCallResult resultEv:
                            {
                                toolNames.TryGetValue(resultEv.ToolCallId, out var name);
                                if (toolArgs.TryGetValue(resultEv.ToolCallId, out var argBuf) && argBuf.Length > 0)
                                {
                                    PublishTrace("tool_args", resultEv.ToolCallId, name, argBuf.ToString());
                                }

                                PublishTrace(
                                    "tool_result",
                                    resultEv.ToolCallId,
                                    name,
                                    string.IsNullOrWhiteSpace(resultEv.Content) ? "（空结果）" : resultEv.Content);
                                break;
                            }

                            case AgentStreamEvent.ReasoningMessageContent reasoningDelta
                                when !string.IsNullOrEmpty(reasoningDelta.Delta):
                                reasoning.Append(reasoningDelta.Delta);
                                break;

                            case AgentStreamEvent.ReasoningMessageEnd:
                                if (reasoning.Length > 0)
                                {
                                    PublishTrace("reasoning", summary: reasoning.ToString());
                                    reasoning.Clear();
                                }

                                break;
                        }

                        return Task.CompletedTask;
                    }
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            session = result;
            MarkRoomMessagesSynced(syncState, trigger.Id, messageId);
            // Pick up human chatter that arrived while this turn was running.
            session = SyncRoomChatIntoSession(session, syncState, roomId, excludeMessageId: null);
            await PersistSessionAsync(session, cancellationToken).ConfigureAwait(false);
            await _sessionStore.SaveSyncStateAsync(roomId, syncState, CancellationToken.None).ConfigureAwait(false);

            var final = result.Messages.LastOrDefault(m => m.Role == MessageRole.Assistant)?.Content;
            if (!string.IsNullOrWhiteSpace(final))
            {
                streamed.Clear();
                streamed.Append(final.Trim());
            }
            else if (streamed.Length == 0)
            {
                streamed.Append("（无文本回复）");
            }

            FlushText(force: true);
            PublishTrace("run_finished", summary: "完成");
        }
        catch (OperationCanceledException)
        {
            MarkRoomMessagesSynced(syncState, trigger.Id, messageId);
            session = SyncRoomChatIntoSession(session, syncState, roomId, excludeMessageId: null);
            await PersistSessionAsync(session, CancellationToken.None).ConfigureAwait(false);
            await _sessionStore.SaveSyncStateAsync(roomId, syncState, CancellationToken.None).ConfigureAwait(false);

            if (streamed.Length == 0 || streamed.ToString().Trim() is "…" or "")
            {
                streamed.Clear();
                streamed.Append("已停止。");
            }
            else
            {
                streamed.Append("\n\n（已停止）");
            }

            FlushText(force: true);
            PublishTrace("cancelled", summary: "房主已停止");
            throw;
        }
        catch (Exception ex)
        {
            MarkRoomMessagesSynced(syncState, trigger.Id, messageId);
            session = SyncRoomChatIntoSession(session, syncState, roomId, excludeMessageId: null);
            await PersistSessionAsync(session, CancellationToken.None).ConfigureAwait(false);
            await _sessionStore.SaveSyncStateAsync(roomId, syncState, CancellationToken.None).ConfigureAwait(false);
            streamed.Clear();
            streamed.Append("处理失败：").Append(ex.Message);
            FlushText(force: true);
            PublishTrace("error", summary: ex.Message);
            throw;
        }
    }

    private AgentSession SyncRoomChatIntoSession(
        AgentSession session,
        RoomAgentSyncState syncState,
        string roomId,
        string? excludeMessageId)
    {
        var synced = new HashSet<string>(syncState.SyncedMessageIds, StringComparer.OrdinalIgnoreCase);
        // Also treat messages already mirrored by id as synced (survives lost sync file).
        foreach (var existing in session.Messages)
        {
            if (existing.Id.StartsWith("room:", StringComparison.Ordinal))
            {
                synced.Add(existing.Id["room:".Length..]);
            }
        }

        var roomMessages = _store.GetMessages(roomId);
        var changed = false;
        foreach (var msg in roomMessages)
        {
            if (!string.IsNullOrWhiteSpace(excludeMessageId)
                && string.Equals(msg.Id, excludeMessageId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (synced.Contains(msg.Id))
            {
                continue;
            }

            if (IsAgentRoomMessage(msg))
            {
                // Prior agent replies are already in the AgentSession from SendAsync.
                synced.Add(msg.Id);
                changed = true;
                continue;
            }

            var content = FormatRoomMessageForSession(msg);
            if (string.IsNullOrWhiteSpace(content))
            {
                synced.Add(msg.Id);
                changed = true;
                continue;
            }

            session = session.WithMessage(
                ChatMessage.CreateWithId(
                    RoomAgentSessionStore.RoomChatMessageId(msg.Id),
                    MessageRole.User,
                    content));
            synced.Add(msg.Id);
            changed = true;
        }

        if (changed)
        {
            syncState.SyncedMessageIds = synced.ToList();
            // Drop ids no longer present in the room to keep the sync file bounded.
            var live = new HashSet<string>(roomMessages.Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
            syncState.SyncedMessageIds = syncState.SyncedMessageIds
                .Where(id => live.Contains(id))
                .ToList();
        }

        return session;
    }

    private static void MarkRoomMessagesSynced(RoomAgentSyncState syncState, params string[] messageIds)
    {
        var synced = new HashSet<string>(syncState.SyncedMessageIds, StringComparer.OrdinalIgnoreCase);
        foreach (var id in messageIds)
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                synced.Add(id);
            }
        }

        syncState.SyncedMessageIds = synced.ToList();
    }

    private static bool IsAgentRoomMessage(RoomMessage message) =>
        string.Equals(message.Kind, "agent", StringComparison.OrdinalIgnoreCase)
        || string.Equals(message.SenderId, RoomAgentIds.SenderId, StringComparison.OrdinalIgnoreCase);

    private static string FormatRoomMessageForSession(RoomMessage msg)
    {
        var preview = msg.Text?.Trim() ?? "";
        if (msg.Attachments.Count > 0)
        {
            var attPreview = string.Join(", ", msg.Attachments.Select(FormatAttachmentForPrompt));
            preview = string.IsNullOrWhiteSpace(preview)
                ? "[attachments: " + attPreview + "]"
                : preview + " [attachments: " + attPreview + "]";
        }

        if (string.IsNullOrWhiteSpace(preview))
        {
            return "";
        }

        return $"[room] {msg.DisplayName}: {preview}";
    }

    private static string? TruncateTrace(string? text, int maxChars = 800)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var trimmed = text.Trim();
        if (trimmed.Length <= maxChars)
        {
            return trimmed;
        }

        return trimmed[..maxChars] + "…";
    }

    private (string Root, string? Id) ResolveRoomWorkspace(RoomInfo? room)
    {
        if (room is not null && !string.IsNullOrWhiteSpace(room.WorkspaceId))
        {
            var match = _settings.Workspaces.FirstOrDefault(w =>
                string.Equals(w.Id, room.WorkspaceId, StringComparison.OrdinalIgnoreCase));
            if (match is not null && !string.IsNullOrWhiteSpace(match.RootPath))
            {
                return (match.RootPath.Trim(), match.Id);
            }
        }

        if (room is not null && !string.IsNullOrWhiteSpace(room.WorkspaceRoot))
        {
            var byRoot = _settings.Workspaces.FirstOrDefault(w =>
                !string.IsNullOrWhiteSpace(w.RootPath)
                && string.Equals(w.RootPath.Trim(), room.WorkspaceRoot.Trim(), StringComparison.OrdinalIgnoreCase));
            return (room.WorkspaceRoot.Trim(), byRoot?.Id ?? room.WorkspaceId);
        }

        // No silent fallback — host must set workspace in room settings.
        return (string.Empty, null);
    }

    private static string BuildSystemPrompt(AgentProfile profile, string workspaceRoot)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are Athlon, a helpful teammate in a LAN chat room.");
        sb.AppendLine("Reply in the same language the user uses. Keep answers concise and conversational.");
        sb.AppendLine("When the user wants a file shared with the room (especially after editing a Word/docx or other document), call room_share_file with the workspace path.");
        if (!string.IsNullOrWhiteSpace(workspaceRoot))
        {
            sb.Append("Your assigned workspace root for this room is: ").Append(workspaceRoot).AppendLine();
            sb.Append("Chat uploads from members are mirrored under: ")
                .Append(Path.Combine(workspaceRoot, ".athlon-room"))
                .AppendLine(" (per-room subfolders). Prefer those absolute paths when analyzing user files.");
            sb.AppendLine("Only read/write files inside this workspace unless the user explicitly asks otherwise.");
        }
        else
        {
            sb.AppendLine(
                "This room has no workspace configured yet. Tell the host to set a workspace path in room settings before file tools can work.");
        }

        var selectedSkills = profile.SkillNames?
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
        if (selectedSkills.Length > 0)
        {
            sb.AppendLine();
            sb.Append("This room has enabled Skills: ")
                .Append(string.Join(", ", selectedSkills))
                .AppendLine(".");
            sb.AppendLine(
                "When a task matches one of them, call load_skill_through_path(skillId=\"…\", path=\"SKILL.md\") before proceeding.");
        }

        if (!string.IsNullOrWhiteSpace(profile.SoulMd))
        {
            sb.AppendLine();
            sb.AppendLine(profile.SoulMd.Trim());
        }

        if (!string.IsNullOrWhiteSpace(profile.AgentMd))
        {
            sb.AppendLine();
            sb.AppendLine(profile.AgentMd.Trim());
        }

        return sb.ToString();
    }

    private static string BuildUserPrompt(RoomMessage trigger, string taskText)
    {
        // Room chatter up to this point was mirrored into the session; this is only the current ask.
        var sb = new StringBuilder();
        sb.Append(trigger.DisplayName).Append(" asked you: ").AppendLine(
            string.IsNullOrWhiteSpace(taskText) ? "(see attachments)" : taskText);
        if (trigger.Attachments.Count > 0)
        {
            sb.AppendLine("Attachments on this message (use these workspace paths with file tools):");
            foreach (var att in trigger.Attachments)
            {
                sb.Append("- ").AppendLine(FormatAttachmentForPrompt(att));
            }
        }

        return sb.ToString();
    }

    private static string FormatAttachmentForPrompt(RoomAttachmentInfo att)
    {
        if (!string.IsNullOrWhiteSpace(att.WorkspacePath))
        {
            return $"{att.FileName} ({att.MimeType}) @ {att.WorkspacePath}";
        }

        return $"{att.FileName} ({att.MimeType})";
    }
}
