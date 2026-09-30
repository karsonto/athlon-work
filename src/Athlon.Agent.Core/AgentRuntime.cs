using Athlon.Agent.Core.BehaviorReport;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.Prompt;
using Athlon.Agent.Core.Streaming;
using Athlon.Agent.Core.Events;
using Athlon.Agent.Core.Middleware;
using Athlon.Agent.Core.SubAgents;
using Athlon.Agent.Core.RuntimeDiagnostics;

namespace Athlon.Agent.Core;

public sealed partial class AgentRuntime(
    IAgentModelClient modelClient,
    IFileStorageService storage,
    IToolRouter toolRouter,
    ISystemPromptOrchestrator systemPromptOrchestrator,
    IToolResultEvictor toolResultEvictor,
    ITokenEstimatorCalibrator tokenEstimatorCalibrator,
    ISessionUsageAccumulator sessionUsageAccumulator,
    IPromptPressureStore promptPressureStore,
    IActiveAgentSessionContext activeSessionContext,
    IAgentRunContextAccessor runContextAccessor,
    AgentTurnMiddlewarePipeline turnPipeline,
    CompactionTurnMiddleware compactionMiddleware,
    AppSettings settings,
    IAppLogger logger,
    IRuntimeDiagnosticEventSink runtimeDiagnosticEventSink,
    IEventManager? eventManager = null,
    IConversationTranscriptWriter? transcriptWriter = null) : IAgentRuntime
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
        eventManager,
        runtimeDiagnosticEventSink);
    private readonly IConversationTranscriptWriter _transcript =
        transcriptWriter ?? new ImmediateConversationTranscriptWriter(storage);
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
        _eventManager,
        runtimeDiagnosticEventSink);

    public async Task<AgentSession> SendAsync(
        AgentSession session,
        string userInput,
        IReadOnlyList<ImageAttachment>? imageAttachments = null,
        AgentTurnCallbacks? callbacks = null,
        CancellationToken cancellationToken = default,
        bool computerUseActive = false,
        AgentLoopOptions? loopOptions = null,
        bool appendUserMessage = true)
    {
        var existing = runContextAccessor.Current;
        AgentRunContext runContext;
        if (existing is { Kind: AgentRunKind.SubAgent }
            && string.Equals(existing.SessionId, session.Id, StringComparison.Ordinal))
        {
            runContext = existing;
            if (runContext.LoopOptions is null && loopOptions is not null)
            {
                runContext = runContext with { LoopOptions = loopOptions };
            }
        }
        else
        {
            var ignorePatterns = ResolveIgnorePatterns(session);
            var workspaceKind = WorkspaceSessionResolver.ResolveKind(session, settings);
            var runId = IdGen.NewId();
            runContext = AgentRunContext.CreateRoot(
                session,
                runId,
                toolRouter,
                systemPromptOrchestrator,
                ignorePatterns,
                workspaceKind,
                computerUseActive);
            if (loopOptions is not null)
            {
                runContext = runContext with { LoopOptions = loopOptions };
            }
        }

        using var runScope = runContextAccessor.Push(runContext);
        using var workspaceScope = SessionWorkspaceScope.Enter(
            runContext.WorkspaceRoot,
            runContext.WorkspaceIgnorePatterns,
            runContext.WorkspaceKind);
        using var skillActivationScope = SessionSkillActivationScope.EnterNewTurn();
        using var sessionScope = activeSessionContext.Enter(session.Id);
        // SSH connections are keyed by the conversation-tree root. Sub-agent turns run nested
        // inside this flow and therefore inherit the root scope instead of replacing it.
        using var sshRootScope = SshRootSessionScope.EnterIfAbsent(session.Id);
        var turnResult = await SendAsyncTurnAsync(session, userInput, imageAttachments, callbacks, runContext, cancellationToken, appendUserMessage).ConfigureAwait(false);
        EmitSkillUsageIfApplicable(turnResult, runContext);
        return turnResult;
    }

    /// <summary>
    /// Emits a <c>skill_usage</c> summary when the just-finished turn activated at least one
    /// skill and executed tool calls. Whole-turn tool outcomes are attributed to the most
    /// recently activated skill. Must run before the turn's skill activation scope is disposed.
    /// </summary>
    private void EmitSkillUsageIfApplicable(AgentSession session, AgentRunContext runContext)
    {
        var state = SessionSkillActivationScope.CurrentState;
        if (state is null || state.TotalToolCalls == 0 || string.IsNullOrWhiteSpace(state.LastActivatedSkillId))
        {
            return;
        }

        _eventManager.Record(
            BehaviorEventIds.SkillUsage,
            BehaviorEventTypes.Event,
            BehaviorEventIds.SkillUsage,
            new Dictionary<string, object?>
            {
                ["skill_id"] = state.LastActivatedSkillId,
                ["session_id"] = session.Id,
                ["run_id"] = runContext.RunId,
                ["total_calls"] = state.TotalToolCalls,
                ["success_calls"] = state.SucceededToolCalls,
                ["failed_calls"] = state.FailedToolCalls
            });
    }

    private IReadOnlyList<string> ResolveIgnorePatterns(AgentSession session) =>
        WorkspaceSessionResolver.ResolveIgnorePatterns(session, settings);

    private async Task<AgentSession> SendAsyncTurnAsync(
        AgentSession session,
        string userInput,
        IReadOnlyList<ImageAttachment>? imageAttachments,
        AgentTurnCallbacks? callbacks,
        AgentRunContext runContext,
        CancellationToken cancellationToken,
        bool appendUserMessage)
    {
        try
        {
            ChatMessage? userMessage = null;
            if (appendUserMessage)
            {
                userMessage = ChatMessage.Create(
                    MessageRole.User,
                    userInput,
                    session.Messages.LastOrDefault()?.Id,
                    imageAttachments: imageAttachments);
                session = session.WithMessage(userMessage);
                await PersistMessageAsync(session, userMessage, cancellationToken).ConfigureAwait(false);
                await NotifySessionUpdatedAsync(callbacks, session).ConfigureAwait(false);
            }

            var parentMessageId = userMessage?.Id ?? session.Messages.LastOrDefault()?.Id;

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

            while (true)
            {
                // Re-resolve tools each model round so mid-turn changes (e.g. browser_navigate
                // opening a Browser tab) unlock IBrowserTool ARIA/page tools for the next call.
                tools = activeRouter.ListTools();
                turnInvocation.Tools = tools;
                var roundFingerprint = ToolCatalogFingerprint.Compute(tools);
                LogToolCatalogDrift(session.Id, roundFingerprint);

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
                    runtimeContextState,
                    turnInvocation.TokenBudgetNotice);
                var runtimeContextForRequest = runtimeContextState.LastSelectedContext;

                var assistantMessageId = IdGen.NewId();
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
                        parentMessageId,
                        reasoningContent: response.ReasoningContent);
                    session = session.WithMessage(assistant);
                    await PersistMessageAsync(session, assistant, cancellationToken).ConfigureAwait(false);
                    await NotifySessionUpdatedAsync(callbacks, session).ConfigureAwait(false);
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
                    parentMessageId,
                    response.ToolCalls,
                    response.ReasoningContent);
                session = session.WithMessage(assistantWithToolCalls);
                await PersistMessageAsync(session, assistantWithToolCalls, cancellationToken).ConfigureAwait(false);
                await NotifySessionUpdatedAsync(callbacks, session).ConfigureAwait(false);
                await PublishStreamEventsAsync(callbacks, streamAdapter.OnAssistantRoundCompleted(assistantWithToolCalls)).ConfigureAwait(false);

                modelToolRound++;
                turnInvocation.State.ModelToolRound = modelToolRound;
                if (maxModelToolRounds is > 0 && modelToolRound >= maxModelToolRounds)
                {
                    _logger.Warning(
                        "Max model tool rounds ({MaxRounds}) reached for session {SessionId}; writing failure tool results and stopping",
                        maxModelToolRounds,
                        session.Id);
                    foreach (var toolCall in response.ToolCalls)
                    {
                        var failure = ToolResult.Failure(
                            "Max model tool rounds reached",
                            $"Tool was not executed because the session reached the max model tool rounds limit ({maxModelToolRounds}).");
                        var content = FormatToolResult(toolCall, failure);
                        var toolMessage = ChatMessage.CreateWithId(
                            ChatMessage.ToolResultMessageId(toolCall.Id),
                            MessageRole.Tool,
                            content,
                            parentMessageId);
                        session = session.WithUpsertedMessage(toolMessage);
                        await PublishStreamEventsAsync(callbacks, streamAdapter.OnToolResult(toolMessage, toolCall)).ConfigureAwait(false);
                        await PersistMessageAsync(session, toolMessage, cancellationToken).ConfigureAwait(false);
                    }

                    await NotifySessionUpdatedAsync(callbacks, session).ConfigureAwait(false);
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

                var endsTurn = false;
                var toolGroups = ParallelToolPolicy.Partition(
                    response.ToolCalls,
                    settings.ParallelToolExecution,
                    ResolveToolRouter());
                foreach (var group in toolGroups)
                {
                    var slice = response.ToolCalls.Skip(group.Start).Take(group.Count).ToArray();
                    if (group.Parallel)
                    {
                        turnInvocation.Session = session;
                        bool groupEndsTurn;
                        (session, groupEndsTurn) = await InvokeParallelToolBatchAsync(
                            turnInvocation,
                            parentMessageId,
                            slice,
                            cancellationToken).ConfigureAwait(false);
                        endsTurn |= groupEndsTurn;
                    }
                    else
                    {
                        foreach (var toolCall in slice)
                        {
                            turnInvocation.Session = session;
                            bool toolEndsTurn;
                            (session, toolEndsTurn) = await InvokeToolAndPersistAsync(
                                turnInvocation,
                                parentMessageId,
                                toolCall,
                                cancellationToken).ConfigureAwait(false);
                            endsTurn |= toolEndsTurn;
                            if (endsTurn)
                            {
                                break;
                            }
                        }
                    }

                    if (endsTurn)
                    {
                        break;
                    }
                }

                if (endsTurn)
                {
                    await NotifySessionUpdatedAsync(callbacks, session).ConfigureAwait(false);
                    await PublishStreamEventsAsync(callbacks, streamAdapter.FinishRun()).ConfigureAwait(false);
                    _logger.Information(
                        "Tool ended turn for session {SessionId} with {MessageCount} messages",
                        session.Id,
                        session.Messages.Count);
                    turnInvocation.Session = session;
                    await turnPipeline.OnTurnCompletedAsync(turnInvocation, cancellationToken).ConfigureAwait(false);
                    await PublishTurnFinishedAsync(callbacks, runContext, session, TurnOutcomeKind.Completed, cancellationToken)
                        .ConfigureAwait(false);
                    await RecordTrainingDataAsync(session, cancellationToken).ConfigureAwait(false);
                    return session;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Mark dirty with a short timeout to avoid hanging on shutdown
            using var saveCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await _transcript.MarkSessionDirtyAsync(session, saveCts.Token).ConfigureAwait(false);
                await NotifySessionUpdatedAsync(callbacks, session).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Save timed out — proceed with cancellation
            }
            throw;
        }
    }

    private static async Task NotifySessionUpdatedAsync(AgentTurnCallbacks? callbacks, AgentSession session)
    {
        if (callbacks?.OnSessionUpdated is { } onSessionUpdated)
        {
            await onSessionUpdated(session).ConfigureAwait(false);
        }
    }

    private async Task<(AgentSession Session, string? TokenBudgetNotice)> RunForceCompactPreCompletionAsync(
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
                ?? AgentRunContext.CreateRoot(session, IdGen.NewId(), toolRouter, systemPromptOrchestrator, ResolveIgnorePatterns(session)),
            Session = session,
            Callbacks = callbacks,
            StreamAdapter = new AgentStreamAdapter(session.Id, IdGen.NewId()),
            EnvironmentPrompt = environmentPrompt,
            RuntimeContext = runtimeContext,
            Tools = tools
        };
        var sessionAfter = await compactionMiddleware.RunPreCompletionAsync(
            invocation,
            options,
            environmentPrompt,
            tools,
            cancellationToken,
            pressureOverride).ConfigureAwait(false);
        return (sessionAfter, invocation.TokenBudgetNotice);
    }


}
