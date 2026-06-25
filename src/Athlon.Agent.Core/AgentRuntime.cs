using System.Text.RegularExpressions;
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
    IPostTurnMemoryProcessor memoryProcessor) : IAgentRuntime
{
    private readonly IAppLogger _logger = logger.ForContext("AgentRuntime");
    private readonly ToolInvocationPipeline _toolPipeline = new(
        storage,
        toolResultEvictor,
        () => runContextAccessor.Current?.ToolRouter ?? toolRouter,
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
        runContextAccessor,
        ResolveSystemPromptOrchestrator,
        RunForceCompactPreCompletionAsync,
        logger);

    public async Task<AgentSession> SendAsync(
        AgentSession session,
        string userInput,
        IReadOnlyList<ImageAttachment>? imageAttachments = null,
        AgentTurnCallbacks? callbacks = null,
        AgentSendOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var ignorePatterns = ResolveIgnorePatterns(session);
        var runId = Guid.NewGuid().ToString("N");
        var loopOptions = ResolveLoopOptions(options);
        var runContext = AgentRunContext.CreateRoot(
            session,
            runId,
            toolRouter,
            systemPromptOrchestrator,
            ignorePatterns) with
        {
            LoopOptions = loopOptions,
            RequireToolApproval = options?.RequireToolApproval
        };
        if (AgentLoopOptionsScope.Current is { } ambientLoopOptions)
        {
            runContext = runContext with { LoopOptions = ambientLoopOptions };
        }
        using var runScope = runContextAccessor.Push(runContext);
        using var workspaceScope = SessionWorkspaceScope.Enter(runContext.WorkspaceRoot, runContext.WorkspaceIgnorePatterns);
        using var skillActivationScope = SessionSkillActivationScope.EnterNewTurn();
        using var sessionScope = activeSessionContext.Enter(session.Id);
        return await SendAsyncTurnAsync(session, userInput, imageAttachments, callbacks, runContext, cancellationToken);
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
            await PersistMessageAsync(session, userMessage, cancellationToken);

            var activeRouter = ResolveToolRouter();
            var activePrompt = ResolveSystemPromptOrchestrator();
            var tools = activeRouter.ListTools();
            var toolCatalogFingerprint = ToolCatalogFingerprint.Compute(tools);
            LogToolCatalogDrift(session.Id, toolCatalogFingerprint);

            var frozenPrompt = activePrompt.PrepareForTurn(session, tools);
            var environmentPrompt = activePrompt.BuildForReasoningIteration(frozenPrompt, session, tools);
            var modelToolRound = 0;
            var maxModelToolRounds = runContext.LoopOptions?.MaxModelToolRounds;
            var modelMessageCache = new ModelMessageCache();
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
            await turnPipeline.OnTurnStartingAsync(turnInvocation, cancellationToken);
            session = turnInvocation.Session;
            if (callbacks?.EventSink is not null)
            {
                await callbacks.EventSink.PublishLifecycleEventAsync(
                    new AgentRunLifecycleEvent.TurnStarted(runContext),
                    cancellationToken).ConfigureAwait(false);
            }
            await PublishStreamEventsAsync(callbacks, streamAdapter.CreateRunStarted());

            if (ShouldListWorkspaceFiles(userInput) && tools.Any(tool => string.Equals(tool.Name, "file_list", StringComparison.OrdinalIgnoreCase)))
            {
                var toolCall = new AgentToolCall(Guid.NewGuid().ToString("N"), "file_list", new Dictionary<string, string>());
                session = await InvokeToolAndPersistAsync(
                    turnInvocation,
                    userMessage.Id,
                    toolCall,
                    cancellationToken);
            }

            while (true)
            {
                turnInvocation.Session = session;
                turnInvocation.EnvironmentPrompt = environmentPrompt;
                await turnPipeline.OnBeforeModelRoundAsync(turnInvocation, cancellationToken);
                session = turnInvocation.Session;
                environmentPrompt = activePrompt.BuildForReasoningIteration(frozenPrompt, session, tools);
                turnInvocation.EnvironmentPrompt = environmentPrompt;
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
                    turnInvocation.Session = session;
                    await turnPipeline.OnTurnCompletedAsync(turnInvocation, cancellationToken);
                    await PublishTurnFinishedAsync(callbacks, runContext, session, TurnOutcomeKind.Completed, cancellationToken);
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
                turnInvocation.State.ModelToolRound = modelToolRound;
                if (maxModelToolRounds is > 0 && modelToolRound >= maxModelToolRounds)
                {
                    _logger.Warning(
                        "Max model tool rounds ({MaxRounds}) reached for session {SessionId}; stopping without executing pending tools",
                        maxModelToolRounds,
                        session.Id);
                    foreach (var pendingCall in response.ToolCalls)
                    {
                        var limitResult = ToolResult.Failure(
                            "Max tool rounds reached",
                            $"Agent stopped after {maxModelToolRounds} model/tool rounds; this tool call was not executed.");
                        var limitContent = FormatToolResult(pendingCall, limitResult);
                        var limitMessage = ChatMessage.Create(MessageRole.Tool, limitContent, userMessage.Id);
                        session = session.WithMessage(limitMessage);
                        await PersistMessageAsync(session, limitMessage, cancellationToken);
                    }

                    await PublishStreamEventsAsync(callbacks, streamAdapter.FinishRun());
                    turnInvocation.Session = session;
                    await turnPipeline.OnTurnCompletedAsync(turnInvocation, cancellationToken);
                    await PublishTurnFinishedAsync(
                        callbacks,
                        runContext,
                        session,
                        TurnOutcomeKind.MaxToolRoundsReached,
                        cancellationToken);
                    return session;
                }

                session = await InvokeToolCallsAsync(
                    turnInvocation,
                    userMessage.Id,
                    response.ToolCalls,
                    cancellationToken);
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

    private async Task<AgentSession> RunForceCompactPreCompletionAsync(
        AgentSession session,
        AgentTurnCallbacks? callbacks,
        PreCompletionOptions options,
        string environmentPrompt,
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

    private async Task<AgentSession> InvokeToolCallsAsync(
        AgentTurnInvocation invocation,
        string? parentMessageId,
        IReadOnlyList<AgentToolCall> toolCalls,
        CancellationToken cancellationToken)
    {
        var index = 0;
        while (index < toolCalls.Count)
        {
            var batch = new List<AgentToolCall>();
            while (index < toolCalls.Count && ToolReadOnlyClassifier.IsReadOnly(toolCalls[index].Name))
            {
                batch.Add(toolCalls[index]);
                index++;
            }

            if (batch.Count > 1)
            {
                var coreTasks = batch.Select(toolCall =>
                    InvokeToolCoreAsync(invocation, toolCall, cancellationToken)).ToArray();
                var coreResults = await Task.WhenAll(coreTasks).ConfigureAwait(false);
                foreach (var (toolCall, result) in coreResults)
                {
                    invocation.Session = await PersistToolResultAsync(
                        invocation,
                        parentMessageId,
                        toolCall,
                        result,
                        cancellationToken);
                }

                continue;
            }

            if (batch.Count == 1)
            {
                invocation.Session = await InvokeToolAndPersistAsync(invocation, parentMessageId, batch[0], cancellationToken);
                continue;
            }

            invocation.Session = await InvokeToolAndPersistAsync(invocation, parentMessageId, toolCalls[index], cancellationToken);
            index++;
        }

        return invocation.Session;
    }

    private AgentLoopOptions? ResolveLoopOptions(AgentSendOptions? options)
    {
        if (options?.LoopOptions is not null)
        {
            return options.LoopOptions;
        }

        var maxRounds = settings.AgentTurn.MaxModelToolRounds;
        return maxRounds > 0
            ? new AgentLoopOptions { MaxModelToolRounds = maxRounds }
            : null;
    }

    private async Task<AgentSession> InvokeToolAndPersistAsync(
        AgentTurnInvocation invocation,
        string? parentMessageId,
        AgentToolCall toolCall,
        CancellationToken cancellationToken)
    {
        var (call, result) = await InvokeToolCoreAsync(invocation, toolCall, cancellationToken).ConfigureAwait(false);
        return await PersistToolResultAsync(invocation, parentMessageId, call, result, cancellationToken);
    }

    private async Task<(AgentToolCall ToolCall, ToolResult Result)> InvokeToolCoreAsync(
        AgentTurnInvocation invocation,
        AgentToolCall toolCall,
        CancellationToken cancellationToken)
    {
        await turnPipeline.OnBeforeToolInvokeAsync(invocation, toolCall, cancellationToken).ConfigureAwait(false);
        var toolStorm = invocation.ToolStorm;

        if (toolStorm is not null && !toolStorm.TryInspect(toolCall, out var reason))
        {
            return (toolCall, ToolResult.Failure(
                "Duplicate tool call suppressed",
                reason ?? "repeat-loop guard suppressed the duplicate tool call."));
        }

        var definition = invocation.Tools?.FirstOrDefault(tool =>
            string.Equals(tool.Name, toolCall.Name, StringComparison.OrdinalIgnoreCase));
        if (definition is not null
            && ToolApprovalGate.IsApprovalRequired(settings, invocation.RunContext, toolCall, definition))
        {
            var pending = ToolInvocationPolicyEnforcer.TryCreatePendingApproval(toolCall, definition)!;
            invocation.State.PendingApproval = pending;
            var callbacks = invocation.Callbacks;

            if (callbacks?.OnToolApprovalRequired is null)
            {
                return (toolCall, ToolApprovalGate.CreateNoHandlerResult());
            }

            var decision = await callbacks.OnToolApprovalRequired(pending, cancellationToken).ConfigureAwait(false);
            invocation.State.PendingApproval = null;
            if (decision == ToolApprovalDecision.Cancelled)
            {
                throw new OperationCanceledException("Tool approval cancelled.");
            }

            if (decision == ToolApprovalDecision.Rejected)
            {
                return (toolCall, ToolApprovalGate.CreateRejectedResult());
            }
        }

        try
        {
            using var outputStream = new AmbientToolOutputStream(invocation.Callbacks, toolCall.Id);
            var result = await Task.Run(
                    () => ResolveToolRouter().InvokeAsync(new ToolInvocation(toolCall.Name, toolCall.Arguments), cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            return (toolCall, result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Tool {ToolName} threw; returning failure to the model", toolCall.Name);
            return (toolCall, ToolResult.Failure("Tool invocation failed", ex.Message));
        }
    }

    private async Task<AgentSession> PersistToolResultAsync(
        AgentTurnInvocation invocation,
        string? parentMessageId,
        AgentToolCall toolCall,
        ToolResult result,
        CancellationToken cancellationToken)
    {
        await storage.AppendToolCallLogAsync(
            invocation.Session.Id,
            new SessionToolCallLogEntry(
                DateTimeOffset.UtcNow,
                toolCall.Id,
                toolCall.Name,
                toolCall.Arguments,
                result.Succeeded,
                result.Summary,
                result.Content,
                result.Error,
                (long)(result.Duration?.TotalMilliseconds ?? 0)),
            cancellationToken);

        invocation.Session = await _toolPipeline.PersistResultAsync(
            invocation.Session,
            parentMessageId,
            toolCall,
            result,
            invocation.StreamAdapter,
            invocation.Callbacks,
            PersistMessageAsync,
            cancellationToken);
        await turnPipeline.OnAfterToolInvokeAsync(invocation, toolCall, cancellationToken).ConfigureAwait(false);
        return invocation.Session;
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
        await storage.AppendConversationMessageAsync(session.Id, message, cancellationToken);
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
            await collector.RecordTurnAsync(session, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.Warning("Training data recording failed: {Error}", ex.Message);
        }
    }

    private static async Task PublishTurnFinishedAsync(
        AgentTurnCallbacks? callbacks,
        AgentRunContext runContext,
        AgentSession session,
        TurnOutcomeKind outcome,
        CancellationToken cancellationToken)
    {
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
