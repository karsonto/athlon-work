using System.Text.RegularExpressions;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.Memory;
using Athlon.Agent.Core.Prompt;
using Athlon.Agent.Core.Streaming;
using Athlon.Agent.Core.SubAgents;

namespace Athlon.Agent.Core;

public sealed class AgentRuntime(
    IAgentModelClient modelClient,
    IFileStorageService storage,
    IToolRouter toolRouter,
    ISystemPromptOrchestrator systemPromptOrchestrator,
    IPreCompletionPipeline preCompletionPipeline,
    IToolResultEvictor toolResultEvictor,
    ITokenEstimatorCalibrator tokenEstimatorCalibrator,
    ISessionUsageAccumulator sessionUsageAccumulator,
    IPromptPressureStore promptPressureStore,
    ISessionToolStormStore sessionToolStormStore,
    IActiveAgentSessionContext activeSessionContext,
    AppSettings settings,
    IAppLogger logger,
    IPostTurnMemoryProcessor memoryProcessor) : IAgentRuntime
{
    private readonly IAppLogger _logger = logger.ForContext("AgentRuntime");
    private readonly ToolInvocationPipeline _toolPipeline = new(
        storage,
        toolResultEvictor,
        () => AmbientToolRouterScope.CurrentRouter ?? toolRouter,
        logger);
    private TrainingData.ITrainingDataCollector? _trainingDataCollector;
    private AgentTurnCoordinator? _turnCoordinator;

    private TrainingData.ITrainingDataCollector? ResolveTrainingDataCollector()
    {
        if (_trainingDataCollector is not null)
            return _trainingDataCollector;

        if (!settings.TrainingData.Enabled)
            return null;

        _trainingDataCollector = new TrainingData.TrainingSampleStore(settings.TrainingData, logger);
        return _trainingDataCollector;
    }
    private AgentTurnCoordinator TurnCoordinator => _turnCoordinator ??= new AgentTurnCoordinator(
        modelClient,
        tokenEstimatorCalibrator,
        sessionUsageAccumulator,
        promptPressureStore,
        settings,
        ResolveSystemPromptOrchestrator,
        RunPreCompletionPipelineAsync,
        logger);

    public async Task<AgentSession> SendAsync(
        AgentSession session,
        string userInput,
        IReadOnlyList<ImageAttachment>? imageAttachments = null,
        AgentTurnCallbacks? callbacks = null,
        CancellationToken cancellationToken = default)
    {
        var ignorePatterns = ResolveIgnorePatterns(session);
        using var workspaceScope = SessionWorkspaceScope.Enter(session.ActiveWorkspace, ignorePatterns);
        using var skillActivationScope = SessionSkillActivationScope.EnterNewTurn();
        using var sessionScope = activeSessionContext.Enter(session.Id);
        return await SendAsyncTurnAsync(session, userInput, imageAttachments, callbacks, cancellationToken);
    }

    private IReadOnlyList<string> ResolveIgnorePatterns(AgentSession session)
    {
        if (!string.IsNullOrWhiteSpace(session.ActiveWorkspace))
        {
            var fullPath = Path.GetFullPath(session.ActiveWorkspace);
            var match = settings.Workspaces.FirstOrDefault(workspace =>
                !string.IsNullOrWhiteSpace(workspace.RootPath)
                && string.Equals(Path.GetFullPath(workspace.RootPath), fullPath, StringComparison.OrdinalIgnoreCase));
            return WorkspaceIgnoreResolver.Resolve(
                workspacePatterns: match?.IgnorePatterns,
                globalPatterns: settings.WorkspaceIgnore.DirectoryNames);
        }

        var configuredWorkspace = settings.Workspaces.FirstOrDefault(workspace => !string.IsNullOrWhiteSpace(workspace.RootPath));
        return WorkspaceIgnoreResolver.Resolve(
            workspacePatterns: configuredWorkspace?.IgnorePatterns,
            globalPatterns: settings.WorkspaceIgnore.DirectoryNames);
    }

    private async Task<AgentSession> SendAsyncTurnAsync(
        AgentSession session,
        string userInput,
        IReadOnlyList<ImageAttachment>? imageAttachments,
        AgentTurnCallbacks? callbacks,
        CancellationToken cancellationToken)
    {
        try
        {
            var userMessage = ChatMessage.Create(
                MessageRole.User,
                userInput,
                session.Messages.LastOrDefault()?.Id,
                imageAttachments: imageAttachments);
            session = session.WithMessage(userMessage);
            await PersistMessageAsync(session, userMessage, cancellationToken);

            var activeRouter = ResolveToolRouter();
            var activePrompt = ResolveSystemPromptOrchestrator();
            var tools = activeRouter.ListTools();
            var toolCatalogFingerprint = ToolCatalogFingerprint.Compute(tools);
            LogToolCatalogDrift(session.Id, toolCatalogFingerprint);

            var frozenPrompt = activePrompt.PrepareForTurn(session, tools);
            var environmentPrompt = activePrompt.BuildForReasoningIteration(frozenPrompt, session, tools);
            var modelToolRound = 0;
            var maxModelToolRounds = AgentLoopOptionsScope.Current?.MaxModelToolRounds;
            var modelMessageCache = new ModelMessageCache();
            var runId = Guid.NewGuid().ToString("N");
            var streamAdapter = new AgentStreamAdapter(session.Id, runId);
            var toolStorm = ResolveToolStormBreaker(session.Id);
            await PublishStreamEventsAsync(callbacks, streamAdapter.CreateRunStarted());

            if (ShouldListWorkspaceFiles(userInput) && tools.Any(tool => string.Equals(tool.Name, "file_list", StringComparison.OrdinalIgnoreCase)))
            {
                var toolCall = new AgentToolCall(Guid.NewGuid().ToString("N"), "file_list", new Dictionary<string, string>());
                session = await InvokeToolAndPersistAsync(
                    session,
                    userMessage.Id,
                    toolCall,
                    streamAdapter,
                    callbacks,
                    toolStorm,
                    cancellationToken);
            }

            while (true)
            {
                var historyBeforePreCompletion = session.Messages;
                session = await RunPreCompletionPipelineAsync(
                    session,
                    callbacks,
                    PreCompletionOptions.AgentLoop,
                    environmentPrompt,
                    tools,
                    cancellationToken: cancellationToken);
                modelMessageCache.NotePreCompletionResult(historyBeforePreCompletion, session.Messages);
                environmentPrompt = activePrompt.BuildForReasoningIteration(frozenPrompt, session, tools);
                var hygieneResult = ModelMessagesForApiBuilder.Build(
                    modelMessageCache,
                    environmentPrompt,
                    session.Messages,
                    settings.ContextCompaction);

                var assistantMessageId = Guid.NewGuid().ToString("N");
                var (updatedSession, response) = await TurnCoordinator.CompleteWithOverflowRetryAsync(
                    session,
                    callbacks,
                    streamAdapter,
                    assistantMessageId,
                    hygieneResult.Messages,
                    tools,
                    frozenPrompt,
                    environmentPrompt,
                    modelMessageCache,
                    hygieneResult.EstimatedSavingsTokens,
                    cancellationToken);
                session = updatedSession;

                if (response.ToolCalls.Count == 0)
                {
                    if (!streamAdapter.State.HasStartedTextMessage(assistantMessageId)
                        && !string.IsNullOrEmpty(response.Content))
                    {
                        await PublishStreamEventsAsync(
                            callbacks,
                            streamAdapter.OnTextDelta(assistantMessageId, response.Content));
                    }

                    if (!streamAdapter.State.HasStartedReasoningMessage(assistantMessageId)
                        && !string.IsNullOrEmpty(response.ReasoningContent))
                    {
                        await PublishStreamEventsAsync(
                            callbacks,
                            streamAdapter.OnReasoningDelta(assistantMessageId, response.ReasoningContent!));
                    }

                    var assistant = ChatMessage.CreateWithId(
                        assistantMessageId,
                        MessageRole.Assistant,
                        response.Content,
                        userMessage.Id,
                        reasoningContent: response.ReasoningContent);
                    session = session.WithMessage(assistant);
                    await PersistMessageAsync(session, assistant, cancellationToken);
                    await PublishStreamEventsAsync(callbacks, streamAdapter.FinishRun());
                    _logger.Information("Saved session {SessionId} with {MessageCount} messages", session.Id, session.Messages.Count);
                    FireAndForgetMemoryFlush(session);
                    await RecordTrainingDataAsync(session, cancellationToken);
                    return session;
                }

                var assistantWithToolCalls = ChatMessage.CreateWithId(
                    assistantMessageId,
                    MessageRole.Assistant,
                    response.Content,
                    userMessage.Id,
                    response.ToolCalls,
                    response.ReasoningContent);
                session = session.WithMessage(assistantWithToolCalls);
                await PersistMessageAsync(session, assistantWithToolCalls, cancellationToken);
                await PublishStreamEventsAsync(callbacks, streamAdapter.OnAssistantRoundCompleted(assistantWithToolCalls));

                modelToolRound++;
                if (maxModelToolRounds is > 0 && modelToolRound >= maxModelToolRounds)
                {
                    _logger.Warning(
                        "Max model tool rounds ({MaxRounds}) reached for session {SessionId}; stopping without executing pending tools",
                        maxModelToolRounds,
                        session.Id);
                    await PublishStreamEventsAsync(callbacks, streamAdapter.FinishRun());
                    FireAndForgetMemoryFlush(session);
                    return session;
                }

                foreach (var toolCall in response.ToolCalls)
                {
                    session = await InvokeToolAndPersistAsync(
                        session,
                        userMessage.Id,
                        toolCall,
                        streamAdapter,
                        callbacks,
                        toolStorm,
                        cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Save with a short timeout to avoid hanging on shutdown
            using var saveCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await storage.SaveSessionAsync(session, saveCts.Token);
            }
            catch (OperationCanceledException)
            {
                // Save timed out — proceed with cancellation
            }
            throw;
        }
    }

    private ToolStormBreaker? ResolveToolStormBreaker(string sessionId)
    {
        var stormSettings = settings.ContextCompaction.ToolStorm;
        if (!stormSettings.Enabled)
        {
            return null;
        }

        return stormSettings.Scope == ToolStormScope.Session
            ? sessionToolStormStore.GetOrCreate(sessionId, stormSettings)
            : new ToolStormBreaker(stormSettings);
    }

    private void LogToolCatalogDrift(string sessionId, string fingerprint)
    {
        var key = $"tool-catalog:{sessionId}";
        if (_toolCatalogFingerprints.TryGetValue(key, out var previous)
            && ToolCatalogFingerprint.IsBreakingChange(previous, fingerprint))
        {
            var snapshot = sessionUsageAccumulator.Get(sessionId);
            if (snapshot.CacheAvailability == PromptCacheAvailability.HitMiss && snapshot.CacheHitRate is >= 0.3)
            {
                _logger.Warning(
                    "Tool catalog breaking change for session {SessionId} ({Previous} -> {Current}); prompt prefix cache may be invalidated (recent cache hit rate {HitRate:P0})",
                    sessionId,
                    previous,
                    fingerprint,
                    snapshot.CacheHitRate.Value);
            }
            else
            {
                _logger.Warning(
                    "Tool catalog fingerprint changed for session {SessionId}: {Previous} -> {Current}",
                    sessionId,
                    previous,
                    fingerprint);
            }
        }

        _toolCatalogFingerprints[key] = fingerprint;
    }

    private readonly Dictionary<string, string> _toolCatalogFingerprints = new(StringComparer.Ordinal);

    private async Task<AgentSession> InvokeToolAndPersistAsync(
        AgentSession session,
        string? parentMessageId,
        AgentToolCall toolCall,
        AgentStreamAdapter streamAdapter,
        AgentTurnCallbacks? callbacks,
        ToolStormBreaker? toolStorm,
        CancellationToken cancellationToken)
    {
        if (toolStorm is not null && !toolStorm.TryInspect(toolCall, out var reason))
        {
            var suppressed = ToolResult.Failure(
                "Duplicate tool call suppressed",
                reason ?? "repeat-loop guard suppressed the duplicate tool call.");
            var content = FormatToolResult(toolCall, suppressed);
            var toolMessage = ChatMessage.Create(MessageRole.Tool, content, parentMessageId);
            session = session.WithMessage(toolMessage);
            await PublishStreamEventsAsync(callbacks, streamAdapter.OnToolResult(toolMessage, toolCall));
            await PersistMessageAsync(session, toolMessage, cancellationToken);
            return session;
        }

        return await _toolPipeline.InvokeAndPersistAsync(
            session,
            parentMessageId,
            toolCall,
            streamAdapter,
            callbacks,
            PersistMessageAsync,
            cancellationToken);
    }

    private async Task<AgentSession> RunPreCompletionPipelineAsync(
        AgentSession session,
        AgentTurnCallbacks? callbacks,
        PreCompletionOptions options,
        string environmentPrompt,
        IReadOnlyList<ToolDefinition> tools,
        ContextPressureLevel pressureOverride = ContextPressureLevel.Normal,
        CancellationToken cancellationToken = default)
    {
        CompactionRuntimeContext? runtimeContext = null;
        var compaction = settings.ContextCompaction;
        if (compaction.Enabled || options.ForceConversationCompact)
        {
            var multiplier = tokenEstimatorCalibrator.GetMultiplier(session.Id);
            var budget = ContextBudgetCalculator.Compute(
                environmentPrompt,
                tools,
                session.Messages,
                settings.ContextCompaction,
                settings.Model,
                multiplier);
            budget = ApplyPromptPressure(budget, session.Id);
            runtimeContext = new CompactionRuntimeContext(
                budget,
                environmentPrompt,
                tools,
                multiplier,
                pressureOverride,
                promptPressureStore.GetLastPromptTokens(session.Id));
        }

        var messageIdsBefore = session.Messages.Select(message => message.Id).ToHashSet(StringComparer.Ordinal);
        session = await preCompletionPipeline.RunAsync(session, options, runtimeContext, cancellationToken);
        return await PersistCompactionAuditsAsync(session, messageIdsBefore, callbacks, cancellationToken);
    }

    private ContextBudgetSnapshot ApplyPromptPressure(ContextBudgetSnapshot budget, string sessionId)
    {
        var lastPromptTokens = promptPressureStore.GetLastPromptTokens(sessionId);
        if (lastPromptTokens is not > 0)
        {
            return budget;
        }

        var historyFromActual = Math.Max(0, lastPromptTokens.Value - budget.FixedOverhead);
        if (historyFromActual <= budget.EstimatedHistory)
        {
            return budget;
        }

        return budget.WithHistoryEstimate(historyFromActual, budget.HistoryBudget);
    }

    private async Task<AgentSession> PersistCompactionAuditsAsync(
        AgentSession session,
        HashSet<string> messageIdsBefore,
        AgentTurnCallbacks? callbacks,
        CancellationToken cancellationToken)
    {
        if (HasCompactionStructureChange(session, messageIdsBefore))
        {
            await NotifySessionUpdatedAsync(callbacks, session);
        }

        foreach (var message in session.Messages)
        {
            if (messageIdsBefore.Contains(message.Id))
            {
                continue;
            }

            await PublishStreamEventsAsync(callbacks, [new AgentStreamEvent.ChatMessageAppended(message)]);
            await PersistMessageAsync(session, message, cancellationToken);
        }

        await storage.SaveSessionAsync(session, cancellationToken);

        return session;
    }

    private async Task PersistMessageAsync(AgentSession session, ChatMessage message, CancellationToken cancellationToken)
    {
        await storage.AppendConversationMessageAsync(session.Id, message, cancellationToken);
    }

    private static bool HasCompactionStructureChange(AgentSession session, HashSet<string> messageIdsBefore)
    {
        var hasNewCompactionAudit = false;
        var hasNewSummaryPlaceholder = false;
        foreach (var message in session.Messages)
        {
            if (messageIdsBefore.Contains(message.Id))
            {
                continue;
            }

            if (message.Role == MessageRole.Compaction)
            {
                hasNewCompactionAudit = true;
            }
            else if (SummaryMessageBuilder.IsSummaryMessage(message))
            {
                hasNewSummaryPlaceholder = true;
            }
        }

        if (hasNewCompactionAudit || hasNewSummaryPlaceholder)
        {
            return true;
        }

        // When count comparison is inconclusive (e.g. count >= before), do a set comparison
        var messageIdsAfter = session.Messages.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        return messageIdsAfter.Count != messageIdsBefore.Count;
    }

    private static async Task NotifySessionUpdatedAsync(AgentTurnCallbacks? callbacks, AgentSession session)
    {
        if (callbacks?.OnSessionUpdated is not null)
        {
            await callbacks.OnSessionUpdated(session);
        }
    }

    internal static async Task PublishStreamEventsAsync(
        AgentTurnCallbacks? callbacks,
        IReadOnlyList<AgentStreamEvent> events)
    {
        if (callbacks?.OnStreamEvent is null || events.Count == 0)
        {
            return;
        }

        foreach (var streamEvent in events)
        {
            await callbacks.OnStreamEvent(streamEvent);
        }
    }

    internal static bool IsContextLengthError(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var message = current.Message;
            if (message.Contains("context_length", StringComparison.OrdinalIgnoreCase)
                || message.Contains("context length", StringComparison.OrdinalIgnoreCase)
                || message.Contains("maximum context", StringComparison.OrdinalIgnoreCase)
                || message.Contains("token limit", StringComparison.OrdinalIgnoreCase)
                || message.Contains("too many tokens", StringComparison.OrdinalIgnoreCase)
                || message.Contains("exceeds the model's maximum", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    internal static List<AgentModelMessage> BuildModelMessages(
        string environmentPrompt,
        IReadOnlyList<ChatMessage> history,
        bool includeReasoningInModelContext = false) =>
        ModelMessageBuilder.BuildModelMessages(environmentPrompt, history, includeReasoningInModelContext);

    public static string FormatToolResult(AgentToolCall call, ToolResult result) =>
        ModelMessageBuilder.FormatToolResult(call, result);

    public static string? ExtractToolCallId(string? content) =>
        ModelMessageBuilder.ExtractToolCallId(content);

    private static bool ShouldListWorkspaceFiles(string userInput)
    {
        if (string.IsNullOrWhiteSpace(userInput))
            return false;

        var input = userInput.Trim();
        // 中文短语直接子串匹配（中文没有词边界概念）
        if (ContainsAny(input, "有哪些文件", "文件列表", "目录下", "目录里", "工作区文件"))
            return true;

        // 英文用正则词边界匹配，防止误触发
        return Regex.IsMatch(input, @"\b(list files|what files|which files)\b", RegexOptions.IgnoreCase);
    }

    private static bool ContainsAny(string value, params string[] terms)
    {
        return terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private async Task RecordTrainingDataAsync(AgentSession session, CancellationToken cancellationToken)
    {
        var collector = ResolveTrainingDataCollector();
        if (collector is null)
            return;

        try
        {
            await collector.RecordTurnAsync(session, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.Warning("Training data recording failed: {Error}", ex.Message);
        }
    }

    private void FireAndForgetMemoryFlush(AgentSession session)
    {
        if (!settings.Memory.Enabled)
            return;

        var capturedSession = session;
        _ = Task.Run(async () =>
        {
            try
            {
                await memoryProcessor.ProcessAsync(capturedSession.Messages, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.Warning("Post-turn memory flush failed: {Error}", ex.Message);
            }
        }, CancellationToken.None);
    }

    private IToolRouter ResolveToolRouter() => AmbientToolRouterScope.CurrentRouter ?? toolRouter;

    private ISystemPromptOrchestrator ResolveSystemPromptOrchestrator() =>
        AmbientSystemPromptOrchestratorScope.CurrentOrchestrator ?? systemPromptOrchestrator;
}
