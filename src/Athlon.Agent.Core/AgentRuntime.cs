using System.Text.RegularExpressions;
using Athlon.Agent.Core.BehaviorReport;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.Memory;
using Athlon.Agent.Core.Prompt;
using Athlon.Agent.Core.Streaming;
using Athlon.Agent.Core.Events;
using Athlon.Agent.Core.Middleware;
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
    IAgentRunContextAccessor runContextAccessor,
    AgentTurnMiddlewarePipeline turnPipeline,
    CompactionTurnMiddleware compactionMiddleware,
    AppSettings settings,
    IAppLogger logger,
    IPostTurnMemoryProcessor memoryProcessor,
    IEventManager? eventManager = null) : IAgentRuntime
{
    private readonly IAppLogger _logger = logger.ForContext("AgentRuntime");
    private readonly IEventManager _eventManager = eventManager ?? NullEventManager.Instance;
    private readonly ToolInvocationPipeline _toolPipeline = new(
        storage,
        toolResultEvictor,
        () => runContextAccessor.Current?.ToolRouter ?? toolRouter,
        runContextAccessor,
        () => settings.ToolPermissions.ApprovalEnabled,
        logger,
        eventManager);
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
        storage,
        settings,
        runContextAccessor,
        RunForceCompactPreCompletionAsync,
        logger,
        _eventManager);

    public async Task<AgentSession> SendAsync(
        AgentSession session,
        string userInput,
        IReadOnlyList<ImageAttachment>? imageAttachments = null,
        AgentTurnCallbacks? callbacks = null,
        CancellationToken cancellationToken = default)
    {
        var ignorePatterns = ResolveIgnorePatterns(session);
        var workspaceKind = WorkspaceSessionResolver.ResolveKind(session, settings);
        var runId = Guid.NewGuid().ToString("N");
        var runContext = AgentRunContext.CreateRoot(
            session,
            runId,
            toolRouter,
            systemPromptOrchestrator,
            ignorePatterns,
            workspaceKind);
        if (AgentLoopOptionsScope.Current is { } loopOptions)
        {
            runContext = runContext with { LoopOptions = loopOptions };
        }
        using var runScope = runContextAccessor.Push(runContext);
        using var workspaceScope = SessionWorkspaceScope.Enter(
            runContext.WorkspaceRoot,
            runContext.WorkspaceIgnorePatterns,
            runContext.WorkspaceKind);
        using var skillActivationScope = SessionSkillActivationScope.EnterNewTurn();
        using var sessionScope = activeSessionContext.Enter(session.Id);
        return await SendAsyncTurnAsync(session, userInput, imageAttachments, callbacks, runContext, cancellationToken).ConfigureAwait(false);
    }

    private IReadOnlyList<string> ResolveIgnorePatterns(AgentSession session) =>
        WorkspaceSessionResolver.ResolveIgnorePatterns(session, settings);

    private async Task<AgentSession> SendAsyncTurnAsync(
        AgentSession session,
        string userInput,
        IReadOnlyList<ImageAttachment>? imageAttachments,
        AgentTurnCallbacks? callbacks,
        AgentRunContext runContext,
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
            await PersistMessageAsync(session, userMessage, cancellationToken).ConfigureAwait(false);

            var activeRouter = ResolveToolRouter();
            var activePrompt = ResolveSystemPromptOrchestrator();
            var tools = activeRouter.ListTools();
            var toolCatalogFingerprint = ToolCatalogFingerprint.Compute(tools);
            LogToolCatalogDrift(session.Id, toolCatalogFingerprint);

            var frozenPrompt = activePrompt.PrepareForTurn(session, tools);
            var environmentPrompt = frozenPrompt.Text;
            var modelToolRound = 0;
            var maxModelToolRounds = runContext.LoopOptions?.MaxModelToolRounds;
            var modelMessageCache = new ModelMessageCache();
            var runtimeContextState = new RuntimeContextInjectionState();
            var streamAdapter = new AgentStreamAdapter(session.Id, runContext.RunId);
            var turnInvocation = new AgentTurnInvocation
            {
                RunContext = runContext,
                Session = session,
                Callbacks = callbacks,
                StreamAdapter = streamAdapter,
                Tools = tools,
                FrozenPrompt = frozenPrompt,
                EnvironmentPrompt = environmentPrompt,
                ModelMessageCache = modelMessageCache
            };
            await turnPipeline.OnTurnStartingAsync(turnInvocation, cancellationToken).ConfigureAwait(false);
            session = turnInvocation.Session;
            if (callbacks?.EventSink is not null)
            {
                await callbacks.EventSink.PublishLifecycleEventAsync(
                    new AgentRunLifecycleEvent.TurnStarted(runContext),
                    cancellationToken).ConfigureAwait(false);
            }
            await PublishStreamEventsAsync(callbacks, streamAdapter.CreateRunStarted()).ConfigureAwait(false);

            if (ShouldListWorkspaceFiles(userInput) && tools.Any(tool => string.Equals(tool.Name, "file_list", StringComparison.OrdinalIgnoreCase)))
            {
                var toolCall = new AgentToolCall(Guid.NewGuid().ToString("N"), "file_list", new Dictionary<string, string>());
                session = await InvokeToolAndPersistAsync(
                    turnInvocation,
                    userMessage.Id,
                    toolCall,
                    cancellationToken).ConfigureAwait(false);
            }

            while (true)
            {
                turnInvocation.Session = session;
                turnInvocation.EnvironmentPrompt = environmentPrompt;
                var runtimeContext = activePrompt.BuildRuntimeContext(session, tools);
                turnInvocation.RuntimeContext = runtimeContext;
                await turnPipeline.OnBeforeModelRoundAsync(turnInvocation, cancellationToken).ConfigureAwait(false);
                session = turnInvocation.Session;
                turnInvocation.EnvironmentPrompt = environmentPrompt;
                var hygieneResult = ModelMessagesForApiBuilder.Build(
                    modelMessageCache,
                    environmentPrompt,
                    session.Messages,
                    settings.ContextCompaction,
                    turnInvocation.RuntimeContext,
                    runtimeContextState);
                var runtimeContextForRequest = runtimeContextState.LastSelectedContext;

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
                    runtimeContextForRequest,
                    cancellationToken).ConfigureAwait(false);
                session = updatedSession;

                if (response.ToolCalls.Count == 0)
                {
                    if (!streamAdapter.State.HasStartedTextMessage(assistantMessageId)
                        && !string.IsNullOrEmpty(response.Content))
                    {
                        await PublishStreamEventsAsync(
                            callbacks,
                            streamAdapter.OnTextDelta(assistantMessageId, response.Content)).ConfigureAwait(false);
                    }

                    if (!streamAdapter.State.HasStartedReasoningMessage(assistantMessageId)
                        && !string.IsNullOrEmpty(response.ReasoningContent))
                    {
                        await PublishStreamEventsAsync(
                            callbacks,
                            streamAdapter.OnReasoningDelta(assistantMessageId, response.ReasoningContent!)).ConfigureAwait(false);
                    }

                    var assistant = ChatMessage.CreateWithId(
                        assistantMessageId,
                        MessageRole.Assistant,
                        response.Content,
                        userMessage.Id,
                        reasoningContent: response.ReasoningContent);
                    session = session.WithMessage(assistant);
                    await PersistMessageAsync(session, assistant, cancellationToken).ConfigureAwait(false);
                    await PublishStreamEventsAsync(callbacks, streamAdapter.FinishRun()).ConfigureAwait(false);
                    _logger.Information("Saved session {SessionId} with {MessageCount} messages", session.Id, session.Messages.Count);
                    turnInvocation.Session = session;
                    await turnPipeline.OnTurnCompletedAsync(turnInvocation, cancellationToken).ConfigureAwait(false);
                    await PublishTurnFinishedAsync(callbacks, runContext, session, TurnOutcomeKind.Completed, cancellationToken).ConfigureAwait(false);
                    await RecordTrainingDataAsync(session, cancellationToken).ConfigureAwait(false);
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
                await PersistMessageAsync(session, assistantWithToolCalls, cancellationToken).ConfigureAwait(false);
                await PublishStreamEventsAsync(callbacks, streamAdapter.OnAssistantRoundCompleted(assistantWithToolCalls)).ConfigureAwait(false);

                modelToolRound++;
                turnInvocation.State.ModelToolRound = modelToolRound;
                if (maxModelToolRounds is > 0 && modelToolRound >= maxModelToolRounds)
                {
                    _logger.Warning(
                        "Max model tool rounds ({MaxRounds}) reached for session {SessionId}; stopping without executing pending tools",
                        maxModelToolRounds,
                        session.Id);
                    await PublishStreamEventsAsync(callbacks, streamAdapter.FinishRun()).ConfigureAwait(false);
                    turnInvocation.Session = session;
                    await turnPipeline.OnTurnCompletedAsync(turnInvocation, cancellationToken).ConfigureAwait(false);
                    await PublishTurnFinishedAsync(
                        callbacks,
                        runContext,
                        session,
                        TurnOutcomeKind.MaxToolRoundsReached,
                        cancellationToken).ConfigureAwait(false);
                    return session;
                }

                if (ParallelToolPolicy.CanParallelizeBatch(response.ToolCalls, settings.ParallelToolExecution))
                {
                    turnInvocation.Session = session;
                    session = await InvokeParallelToolBatchAsync(
                        turnInvocation,
                        userMessage.Id,
                        response.ToolCalls,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    foreach (var toolCall in response.ToolCalls)
                    {
                        turnInvocation.Session = session;
                        session = await InvokeToolAndPersistAsync(
                            turnInvocation,
                            userMessage.Id,
                            toolCall,
                            cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Save with a short timeout to avoid hanging on shutdown
            using var saveCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await storage.SaveSessionAsync(session, saveCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Save timed out — proceed with cancellation
            }
            throw;
        }
    }

    private async Task<AgentSession> RunForceCompactPreCompletionAsync(
        AgentSession session,
        AgentTurnCallbacks? callbacks,
        PreCompletionOptions options,
        string environmentPrompt,
        string? runtimeContext,
        IReadOnlyList<ToolDefinition> tools,
        ContextPressureLevel pressureOverride,
        CancellationToken cancellationToken)
    {
        var invocation = new AgentTurnInvocation
        {
            RunContext = runContextAccessor.Current
                ?? AgentRunContext.CreateRoot(session, Guid.NewGuid().ToString("N"), toolRouter, systemPromptOrchestrator, ResolveIgnorePatterns(session)),
            Session = session,
            Callbacks = callbacks,
            StreamAdapter = new AgentStreamAdapter(session.Id, Guid.NewGuid().ToString("N")),
            EnvironmentPrompt = environmentPrompt,
            RuntimeContext = runtimeContext,
            Tools = tools
        };
        return await compactionMiddleware.RunPreCompletionAsync(
            invocation,
            options,
            environmentPrompt,
            tools,
            cancellationToken,
            pressureOverride).ConfigureAwait(false);
    }

    private async Task<AgentSession> InvokeToolAndPersistAsync(
        AgentTurnInvocation invocation,
        string? parentMessageId,
        AgentToolCall toolCall,
        CancellationToken cancellationToken)
    {
        await turnPipeline.OnBeforeToolInvokeAsync(invocation, toolCall, cancellationToken).ConfigureAwait(false);
        var toolStorm = invocation.ToolStorm;
        var session = invocation.Session;
        var streamAdapter = invocation.StreamAdapter;
        var callbacks = invocation.Callbacks;

        if (toolStorm is not null && !toolStorm.TryInspect(toolCall, out var reason))
        {
            var suppressed = ToolResult.Failure(
                "Duplicate tool call suppressed",
                reason ?? "repeat-loop guard suppressed the duplicate tool call.");
            var content = FormatToolResult(toolCall, suppressed);
            var toolMessage = ChatMessage.Create(MessageRole.Tool, content, parentMessageId);
            session = session.WithMessage(toolMessage);
            await PublishStreamEventsAsync(callbacks, streamAdapter.OnToolResult(toolMessage, toolCall)).ConfigureAwait(false);
            await PersistMessageAsync(session, toolMessage, cancellationToken).ConfigureAwait(false);
            invocation.Session = session;
            await turnPipeline.OnAfterToolInvokeAsync(invocation, toolCall, cancellationToken).ConfigureAwait(false);
            return session;
        }

        session = await _toolPipeline.InvokeAndPersistAsync(
            session,
            parentMessageId,
            toolCall,
            streamAdapter,
            callbacks,
            PersistMessageAsync,
            cancellationToken).ConfigureAwait(false);
        invocation.Session = session;
        await turnPipeline.OnAfterToolInvokeAsync(invocation, toolCall, cancellationToken).ConfigureAwait(false);
        return session;
    }

    private async Task<AgentSession> InvokeParallelToolBatchAsync(
        AgentTurnInvocation invocation,
        string? parentMessageId,
        IReadOnlyList<AgentToolCall> toolCalls,
        CancellationToken cancellationToken)
    {
        var toolStorm = invocation.ToolStorm;
        var results = new ToolResult?[toolCalls.Count];
        var pending = new List<(int Index, AgentToolCall Call)>();

        for (var index = 0; index < toolCalls.Count; index++)
        {
            var toolCall = toolCalls[index];
            if (toolStorm is not null && !toolStorm.TryInspect(toolCall, out var reason))
            {
                results[index] = ToolResult.Failure(
                    "Duplicate tool call suppressed",
                    reason ?? "repeat-loop guard suppressed the duplicate tool call.");
            }
            else
            {
                pending.Add((index, toolCall));
            }
        }

        if (pending.Count > 0)
        {
            var maxDegree = Math.Max(1, settings.ParallelToolExecution.MaxDegreeOfParallelism);
            await Parallel.ForEachAsync(
                pending,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = maxDegree,
                    CancellationToken = cancellationToken
                },
                async (item, ct) =>
                {
                    var outcome = await _toolPipeline.InvokeCoreAsync(
                        invocation.Session.Id,
                        item.Call,
                        invocation.Callbacks,
                        ct).ConfigureAwait(false);
                    results[item.Index] = outcome.Result;
                }).ConfigureAwait(false);
        }

        var session = invocation.Session;
        for (var index = 0; index < toolCalls.Count; index++)
        {
            var toolCall = toolCalls[index];
            await turnPipeline.OnBeforeToolInvokeAsync(invocation, toolCall, cancellationToken).ConfigureAwait(false);

            var result = results[index]
                ?? ToolResult.Failure("Tool invocation failed", "No result was produced for this tool call.");
            session = await _toolPipeline.PersistToolResultAsync(
                session,
                parentMessageId,
                toolCall,
                result,
                invocation.StreamAdapter,
                invocation.Callbacks,
                PersistMessageAsync,
                cancellationToken).ConfigureAwait(false);
            invocation.Session = session;
            await turnPipeline.OnAfterToolInvokeAsync(invocation, toolCall, cancellationToken).ConfigureAwait(false);
        }

        return session;
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

    private async Task PersistMessageAsync(AgentSession session, ChatMessage message, CancellationToken cancellationToken)
    {
        await storage.AppendConversationMessageAsync(session.Id, message, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task PublishStreamEventsAsync(
        AgentTurnCallbacks? callbacks,
        IReadOnlyList<AgentStreamEvent> events)
    {
        if (events.Count == 0)
        {
            return;
        }

        var sink = callbacks?.EventSink;
        if (callbacks?.OnStreamEvent is null && sink is null)
        {
            return;
        }

        foreach (var streamEvent in events)
        {
            if (sink is not null)
            {
                await sink.PublishStreamEventAsync(streamEvent).ConfigureAwait(false);
            }
            else if (callbacks?.OnStreamEvent is { } onStreamEvent)
            {
                await onStreamEvent(streamEvent).ConfigureAwait(false);
            }
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
            await collector.RecordTurnAsync(session, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warning("Training data recording failed: {Error}", ex.Message);
        }
    }

    private async Task PublishTurnFinishedAsync(
        AgentTurnCallbacks? callbacks,
        AgentRunContext runContext,
        AgentSession session,
        TurnOutcomeKind outcome,
        CancellationToken cancellationToken)
    {
        if (outcome == TurnOutcomeKind.MaxToolRoundsReached)
        {
            _eventManager.Record(
                BehaviorEventIds.Turn,
                BehaviorEventTypes.Event,
                BehaviorEventIds.Turn,
                new Dictionary<string, object?>
                {
                    ["session_id"] = session.Id,
                    ["run_id"] = runContext.RunId,
                    ["outcome"] = "max_tool_rounds"
                });
        }

        if (callbacks?.EventSink is null)
        {
            return;
        }

        await callbacks.EventSink.PublishLifecycleEventAsync(
            new AgentRunLifecycleEvent.TurnFinished(runContext, session, new TurnOutcome(outcome)),
            cancellationToken).ConfigureAwait(false);
    }

    private IToolRouter ResolveToolRouter() => runContextAccessor.Current?.ToolRouter ?? toolRouter;

    private ISystemPromptOrchestrator ResolveSystemPromptOrchestrator() =>
        runContextAccessor.Current?.PromptOrchestrator ?? systemPromptOrchestrator;
}
