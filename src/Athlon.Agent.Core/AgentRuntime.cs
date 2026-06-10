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
    private AgentTurnCoordinator? _turnCoordinator;
    private AgentTurnCoordinator TurnCoordinator => _turnCoordinator ??= new AgentTurnCoordinator(
        modelClient,
        tokenEstimatorCalibrator,
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
        return await SendAsyncCore(session, userInput, imageAttachments, callbacks, cancellationToken);
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

    private Task<AgentSession> SendAsyncCore(
        AgentSession session,
        string userInput,
        IReadOnlyList<ImageAttachment>? imageAttachments,
        AgentTurnCallbacks? callbacks,
        CancellationToken cancellationToken) =>
        SendAsyncTurnAsync(session, userInput, imageAttachments, callbacks, cancellationToken);

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
            var frozenPrompt = activePrompt.PrepareForTurn(session, tools);
            var environmentPrompt = activePrompt.BuildForReasoningIteration(frozenPrompt, session, tools);
            var modelToolRound = 0;
            var maxModelToolRounds = AgentLoopOptionsScope.Current?.MaxModelToolRounds;
            var modelMessages = BuildModelMessagesForSession(environmentPrompt, session.Messages);
            var runId = Guid.NewGuid().ToString("N");
            var streamAdapter = new AgentStreamAdapter(session.Id, runId);
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
                    cancellationToken);
            }

            while (true)
            {
                session = await RunPreCompletionPipelineAsync(
                    session,
                    callbacks,
                    PreCompletionOptions.AgentLoop,
                    environmentPrompt,
                    tools,
                    cancellationToken: cancellationToken);
                environmentPrompt = activePrompt.BuildForReasoningIteration(frozenPrompt, session, tools);
                modelMessages = BuildModelMessagesForSession(environmentPrompt, session.Messages);

                var assistantMessageId = Guid.NewGuid().ToString("N");
                var (updatedSession, response) = await TurnCoordinator.CompleteWithOverflowRetryAsync(
                    session,
                    callbacks,
                    streamAdapter,
                    assistantMessageId,
                    modelMessages,
                    tools,
                    frozenPrompt,
                    environmentPrompt,
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

                environmentPrompt = activePrompt.BuildForReasoningIteration(frozenPrompt, session, tools);
                modelMessages = BuildModelMessagesForSession(environmentPrompt, session.Messages);
                foreach (var toolCall in response.ToolCalls)
                {
                    session = await InvokeToolAndPersistAsync(
                        session,
                        userMessage.Id,
                        toolCall,
                        streamAdapter,
                        callbacks,
                        cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            await storage.SaveSessionAsync(session, CancellationToken.None);
            throw;
        }
    }

    private Task<AgentSession> InvokeToolAndPersistAsync(
        AgentSession session,
        string? parentMessageId,
        AgentToolCall toolCall,
        AgentStreamAdapter streamAdapter,
        AgentTurnCallbacks? callbacks,
        CancellationToken cancellationToken) =>
        _toolPipeline.InvokeAndPersistAsync(
            session,
            parentMessageId,
            toolCall,
            streamAdapter,
            callbacks,
            PersistMessageAsync,
            cancellationToken);

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
        if (compaction.Enabled && compaction.DynamicCompaction.Enabled)
        {
            var multiplier = tokenEstimatorCalibrator.GetMultiplier(session.Id);
            var budget = ContextBudgetCalculator.Compute(
                environmentPrompt,
                tools,
                session.Messages,
                settings.ContextCompaction,
                settings.Model,
                multiplier);
            runtimeContext = new CompactionRuntimeContext(
                budget,
                environmentPrompt,
                tools,
                multiplier,
                pressureOverride);
        }

        var messageIdsBefore = session.Messages.Select(message => message.Id).ToHashSet(StringComparer.Ordinal);
        session = await preCompletionPipeline.RunAsync(session, options, runtimeContext, cancellationToken);
        return await PersistCompactionAuditsAsync(session, messageIdsBefore, callbacks, cancellationToken);
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

        var persistedNew = false;
        foreach (var message in session.Messages)
        {
            if (messageIdsBefore.Contains(message.Id))
            {
                continue;
            }

            persistedNew = true;
            await PublishStreamEventsAsync(callbacks, [new AgentStreamEvent.ChatMessageAppended(message)]);
            await PersistMessageAsync(session, message, cancellationToken);
        }

        if (!persistedNew)
        {
            await storage.SaveSessionAsync(session, cancellationToken);
        }

        return session;
    }

    private async Task PersistMessageAsync(AgentSession session, ChatMessage message, CancellationToken cancellationToken)
    {
        await storage.AppendConversationMessageAsync(session.Id, message, cancellationToken);
        await storage.SaveSessionAsync(session, cancellationToken);
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

        return session.Messages.Count < messageIdsBefore.Count;
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

    private List<AgentModelMessage> BuildModelMessagesForSession(string environmentPrompt, IReadOnlyList<ChatMessage> history) =>
        ModelMessageBuilder.BuildForSession(environmentPrompt, history, settings.ContextCompaction.IncludeReasoningInModelContext);

    internal static List<AgentModelMessage> BuildModelMessages(
        string environmentPrompt,
        IReadOnlyList<ChatMessage> history,
        bool includeReasoningInModelContext = false) =>
        ModelMessageBuilder.BuildModelMessages(environmentPrompt, history, includeReasoningInModelContext);

    public static string FormatToolResult(AgentToolCall call, ToolResult result) =>
        ModelMessageBuilder.FormatToolResult(call, result);

    private static bool ShouldListWorkspaceFiles(string userInput)
    {
        var input = userInput.Trim();
        return ContainsAny(input, "有哪些文件", "什么文件", "文件列表", "目录下", "目录里", "工作区文件", "list files", "what files", "which files");
    }

    private static bool ContainsAny(string value, params string[] terms)
    {
        return terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
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
