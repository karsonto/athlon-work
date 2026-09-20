using Athlon.Agent.App.Services.Diagnostics;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;

namespace Athlon.Agent.App.Services;

/// <summary>
/// Display hydration / rebuild: turning the durable transcript page into the
/// <see cref="SessionTurnUiController.Messages"/> view-model list and re-syncing the chat surface.
/// Split out of the orchestration file so the "how a page is rendered" code stays separate from
/// turn orchestration, streaming and approvals.
/// </summary>
public sealed partial class SessionTurnUiController
{
    public Task HydrateFromSessionAsync(AgentSession session) =>
        RebuildDisplayFromMessagesAsync(session.Messages, synthesizeInterruptedToolResults: true);

    public async Task HydrateDisplayAsync(
        AgentSession session,
        IReadOnlyList<ChatMessage> displayMessages,
        bool synthesizeInterruptedToolResults = true,
        IReadOnlyList<ChatMessage>? activitySourceMessages = null,
        bool preserveActiveTurn = false)
    {
        // Profiled only while a session switch is in flight; a no-op otherwise.
        using var profileScope = SessionSwitchProfiler.Measure(SessionSwitchPhases.VmRebuild);
        await RebuildDisplayFromMessagesAsync(
            displayMessages,
            synthesizeInterruptedToolResults,
            activitySourceMessages,
            preserveActiveTurn).ConfigureAwait(true);
    }

    /// <summary>
    /// Rebuild the current display page after settings that affect rendering (e.g. show tool calls),
    /// without pulling the full <see cref="AgentSession.Messages"/> into the UI.
    /// </summary>
    public Task RefreshDisplayForSettingsAsync() =>
        RunOnUiAsync(async () =>
        {
            var displayedIds = new HashSet<string>(
                Messages
                    .Where(message => !message.IsHiddenPlaceholder)
                    .Select(message => message.MessageId),
                StringComparer.Ordinal);
            var activity = _activitySourceMessages.ToList();
            var display = activity.Count > 0 && displayedIds.Count > 0
                ? activity.Where(message => displayedIds.Contains(message.Id)).ToList()
                : activity;
            if (display.Count == 0)
            {
                display = activity;
            }

            await RebuildDisplayFromMessagesCoreAsync(
                    display,
                    synthesizeInterruptedToolResults: true,
                    activity,
                    // Settings/theme changes are a refresh, not an authoritative render: while a
                    // turn is in flight the reload is deferred so replay cannot stack a twin card.
                    authoritativeRender: false)
                .ConfigureAwait(true);
        });

    public void HydrateFromSession(AgentSession session) =>
        RunOnUiSync(() => RebuildDisplayFromMessages(session.Messages, synthesizeInterruptedToolResults: true));

    public void HydrateDisplay(
        AgentSession session,
        IReadOnlyList<ChatMessage> displayMessages,
        bool synthesizeInterruptedToolResults = true,
        IReadOnlyList<ChatMessage>? activitySourceMessages = null,
        bool preserveActiveTurn = false) =>
        RunOnUiSync(() => RebuildDisplayFromMessages(
            displayMessages,
            synthesizeInterruptedToolResults,
            activitySourceMessages,
            preserveActiveTurn));

    private Task RebuildDisplayFromMessagesAsync(
        IReadOnlyList<ChatMessage> displayMessages,
        bool synthesizeInterruptedToolResults,
        IReadOnlyList<ChatMessage>? activitySourceMessages = null,
        bool preserveActiveTurn = false) =>
        RunOnUiAsync(async () =>
        {
            await RebuildDisplayFromMessagesCoreAsync(
                    displayMessages,
                    synthesizeInterruptedToolResults,
                    activitySourceMessages,
                    preserveActiveTurn)
                .ConfigureAwait(true);
        });

    private void RebuildDisplayFromMessages(
        IReadOnlyList<ChatMessage> displayMessages,
        bool synthesizeInterruptedToolResults,
        IReadOnlyList<ChatMessage>? activitySourceMessages = null,
        bool preserveActiveTurn = false)
    {
        var viewModels = BeginRebuildDisplay(
            displayMessages,
            synthesizeInterruptedToolResults,
            activitySourceMessages,
            preserveActiveTurn);
        foreach (var viewModel in viewModels)
        {
            Messages.Add(viewModel);
        }

        FinishRebuildDisplay(viewModels, preserveActiveTurn);
    }

    private async Task RebuildDisplayFromMessagesCoreAsync(
        IReadOnlyList<ChatMessage> displayMessages,
        bool synthesizeInterruptedToolResults,
        IReadOnlyList<ChatMessage>? activitySourceMessages = null,
        bool preserveActiveTurn = false,
        bool authoritativeRender = true)
    {
        var viewModels = BeginRebuildDisplay(
            displayMessages,
            synthesizeInterruptedToolResults,
            activitySourceMessages,
            preserveActiveTurn);
        const int batchSize = ConversationDisplayLimits.UiHydrateBatchSize;
        for (var i = 0; i < viewModels.Count; i++)
        {
            Messages.Add(viewModels[i]);
            if (i > 0 && i % batchSize == 0)
            {
                await DispatcherYieldAsync().ConfigureAwait(true);
            }
        }

        FinishRebuildDisplay(viewModels, preserveActiveTurn, authoritativeRender);
    }

    private IReadOnlyList<ChatMessageViewModel> BeginRebuildDisplay(
        IReadOnlyList<ChatMessage> displayMessages,
        bool synthesizeInterruptedToolResults,
        IReadOnlyList<ChatMessage>? activitySourceMessages = null,
        bool preserveActiveTurn = false)
    {
        _bulkChatViewSyncDepth++;
        if (preserveActiveTurn)
        {
            FlushBufferedStreamingToUi();
        }

        LiveTurnPreserveSnapshot? liveTurn = null;
        if (preserveActiveTurn)
        {
            liveTurn = CaptureLiveTurnPreserveSnapshot();
            _tokenBuffer.ClearBuffers();
        }

        Messages.Clear();
        _streaming.Reset();
        _displayCoordinator.Reset();
        if (!preserveActiveTurn)
        {
            _tokenBuffer.ClearBuffers();
        }

        _displayMessages = displayMessages.ToList();
        _activitySourceMessages = (activitySourceMessages ?? displayMessages).ToList();

        // Prune cache: remove entries that belong to old sessions (not in the new display list)
        var currentIds = new HashSet<string>(displayMessages.Select(m => m.Id), StringComparer.Ordinal);
        if (liveTurn?.AssistantMessageId is { } liveAssistantId)
        {
            currentIds.Add(liveAssistantId);
        }

        var staleKeys = _viewModelCache.Keys.Where(k => !currentIds.Contains(k)).ToList();
        foreach (var key in staleKeys)
        {
            _viewModelCache.Remove(key);
        }

        IReadOnlyList<ChatMessageViewModel> built = ChatTimelineHydrator.BuildDisplayMessages(
            displayMessages,
            _viewModelCache,
            _showToolCalls(),
            synthesizeInterruptedToolResults);

        if (liveTurn is not null)
        {
            built = RestoreLiveTurnPreserveSnapshot(liveTurn, built);
        }

        return built;
    }

    private void FinishRebuildDisplay(
        IReadOnlyList<ChatMessageViewModel> viewModels,
        bool preserveActiveTurn = false,
        bool authoritativeRender = true)
    {
        // Cache ViewModels for future session switches
        foreach (var viewModel in viewModels)
        {
            _viewModelCache[viewModel.MessageId] = viewModel;
        }

        TrimMessagesIfNeeded();
        var fileSource = _activitySourceMessages.Count > 0
            ? _activitySourceMessages.Select(message => new ChatMessageViewModel(message)).ToList()
            : viewModels;
        _modifiedFilesTracker.RebuildFromMessages(fileSource);

        // Idle / history hydrate: replay owns the per-turn cards. Keep live paths only while a turn
        // is still in flight so the mid-turn re-publish can rewrite the replayed entry in place.
        var liveTurnActive = preserveActiveTurn
            || _streaming.ActiveAssistantBubble is not null
            || _streaming.ToolBubblesByIndex.Count > 0
            || _turnActivityTracker.HasSegmentContent;
        if (!liveTurnActive)
        {
            _modifiedFilesTracker.Clear();
        }
        else
        {
            // A switch-back rebuilds the activity source from disk, whose turn anchor differs from
            // the provisional id AddUserMessage minted. Adopt the transcript's id before the
            // authoritative replay below emits its cards, otherwise the live fold lands beside them.
            ReanchorLiveTurnToTranscript();
        }

        _bulkChatViewSyncDepth--;
        if (preserveActiveTurn)
        {
            FlushBufferedStreamingToUi();
        }

        SyncChatView(immediate: true, authoritative: authoritativeRender);
        RequestScrollImmediate();
    }

    private sealed record LiveTurnPreserveSnapshot(
        string? AssistantMessageId,
        string AssistantContent,
        string AssistantReasoning,
        bool HasAssistant);

    private LiveTurnPreserveSnapshot CaptureLiveTurnPreserveSnapshot()
    {
        var (pendingTokens, pendingReasoning, textMessageId, _) = _tokenBuffer.PeekPending();
        var assistant = _streaming.ActiveAssistantBubble;
        var assistantId = assistant?.MessageId ?? textMessageId;
        var content = assistant?.Content ?? string.Empty;
        if (pendingTokens.Length > 0)
        {
            content += pendingTokens;
        }

        var reasoning = assistant?.ReasoningContent ?? string.Empty;
        if (pendingReasoning.Length > 0)
        {
            reasoning += pendingReasoning;
        }

        return new LiveTurnPreserveSnapshot(
            assistantId,
            content,
            reasoning,
            HasAssistant: !string.IsNullOrWhiteSpace(assistantId)
                && (!string.IsNullOrWhiteSpace(content) || !string.IsNullOrWhiteSpace(reasoning) || assistant is not null));
    }

    private IReadOnlyList<ChatMessageViewModel> RestoreLiveTurnPreserveSnapshot(
        LiveTurnPreserveSnapshot liveTurn,
        IReadOnlyList<ChatMessageViewModel> built)
    {
        if (!liveTurn.HasAssistant || string.IsNullOrWhiteSpace(liveTurn.AssistantMessageId))
        {
            return built;
        }

        var list = built as List<ChatMessageViewModel> ?? built.ToList();
        var existing = list.LastOrDefault(message =>
            string.Equals(message.MessageId, liveTurn.AssistantMessageId, StringComparison.Ordinal));
        if (existing is not null)
        {
            if (liveTurn.AssistantContent.Length >= existing.Content.Length
                || !string.IsNullOrWhiteSpace(liveTurn.AssistantReasoning))
            {
                existing.ReplaceStreamingContent(liveTurn.AssistantContent, liveTurn.AssistantReasoning);
            }
            else
            {
                existing.ReplaceStreamingContent(existing.Content, existing.ReasoningContent);
            }

            _streaming.AttachActiveAssistantBubble(existing);
            return list;
        }

        var viewModel = ChatMessageViewModel.CreateStreamingAssistant(liveTurn.AssistantMessageId);
        viewModel.ReplaceStreamingContent(liveTurn.AssistantContent, liveTurn.AssistantReasoning);
        _viewModelCache[viewModel.MessageId] = viewModel;
        list.Add(viewModel);
        _streaming.AttachActiveAssistantBubble(viewModel);
        return list;
    }
}
