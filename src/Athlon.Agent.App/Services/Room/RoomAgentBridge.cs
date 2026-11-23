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
    private readonly IAgentRuntime _runtime;
    private readonly IServiceProvider _services;
    private readonly IActiveWorkspaceContext _workspaceContext;
    private readonly AppSettings _settings;
    private readonly ILogger<RoomAgentBridge> _logger;
    private readonly ConcurrentDictionary<string, byte> _attachedRooms = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _roomGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IDisposable> _subscriptions = new(StringComparer.OrdinalIgnoreCase);

    public RoomAgentBridge(
        RoomStore store,
        AthlonRoomServer server,
        RoomAgentProfileStore profileStore,
        IAgentRuntime runtime,
        IServiceProvider services,
        IActiveWorkspaceContext workspaceContext,
        AppSettings settings,
        ILogger<RoomAgentBridge> logger)
    {
        _store = store;
        _server = server;
        _profileStore = profileStore;
        _runtime = runtime;
        _services = services;
        _workspaceContext = workspaceContext;
        _settings = settings;
        _logger = logger;
        _server.SetRoomLifecycleHooks(AttachRoom, DetachRoom);
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
        try
        {
            await RunAgentTurnAsync(roomId, message, taskText).ConfigureAwait(false);
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
            gate.Release();
        }
    }

    private async Task RunAgentTurnAsync(string roomId, RoomMessage trigger, string taskText)
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

        var history = _store.GetMessages(roomId);
        var prompt = BuildUserPrompt(trigger, taskText, history);

        var room = _store.GetRoom(roomId);
        var (workspaceRoot, workspaceId) = ResolveRoomWorkspace(room);
        var session = AgentSession.Create($"Room {roomId}")
            .WithWorkspace(workspaceRoot, workspaceId);

        var allowList = profile.ToolAllowList.Count > 0
            ? profile.ToolAllowList.ToArray()
            : RoomAgentProfileStore.CatalogTools;
        if (!allowList.Contains("room_share_file", StringComparer.OrdinalIgnoreCase))
        {
            allowList = allowList.Append("room_share_file").ToArray();
        }

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

        using var roomScope = RoomAgentTurnScope.Enter(new RoomAgentTurnOptions(roomId));
        using var profileScope = AgentProfileTurnScope.Enter(profileOptions);
        using var runScope = runContextAccessor.Push(member);

        try
        {
            var result = await _runtime.SendAsync(
                session,
                prompt,
                callbacks: new AgentTurnCallbacks
                {
                    OnSessionUpdated = _ => Task.CompletedTask,
                    OnToolApprovalRequested = static (_, _) =>
                        Task.FromResult(ToolApprovalDecision.Approved),
                    OnStreamEvent = ev =>
                    {
                        if (ev is AgentStreamEvent.TextMessageContent content
                            && !string.IsNullOrEmpty(content.Delta))
                        {
                            streamed.Append(content.Delta);
                            FlushText(force: false);
                        }

                        return Task.CompletedTask;
                    }
                }).ConfigureAwait(false);

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
        }
        catch (Exception ex)
        {
            streamed.Clear();
            streamed.Append("处理失败：").Append(ex.Message);
            FlushText(force: true);
            throw;
        }
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

        if (!string.IsNullOrWhiteSpace(_workspaceContext.RootPath))
        {
            return (_workspaceContext.RootPath, _workspaceContext.WorkspaceId);
        }

        var first = _settings.Workspaces
            .FirstOrDefault(w => !string.IsNullOrWhiteSpace(w.RootPath));
        if (first is not null)
        {
            return (first.RootPath.Trim(), first.Id);
        }

        return (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), null);
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

    private static string BuildUserPrompt(RoomMessage trigger, string taskText, IReadOnlyList<RoomMessage> history)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Recent room messages:");
        foreach (var msg in history.TakeLast(20))
        {
            if (string.Equals(msg.Id, trigger.Id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var preview = msg.Text;
            if (msg.Attachments.Count > 0)
            {
                var attPreview = string.Join(", ", msg.Attachments.Select(FormatAttachmentForPrompt));
                preview = string.IsNullOrWhiteSpace(preview)
                    ? "[attachments: " + attPreview + "]"
                    : preview.Trim() + " [attachments: " + attPreview + "]";
            }

            if (string.IsNullOrWhiteSpace(preview))
            {
                continue;
            }

            sb.Append(msg.DisplayName).Append(": ").AppendLine(preview.Trim());
        }

        sb.AppendLine();
        sb.Append(trigger.DisplayName).Append(" asked you: ").AppendLine(taskText);
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
