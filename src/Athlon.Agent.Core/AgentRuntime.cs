using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json.Serialization;
using Athlon.Agent.Core.Compaction;
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
    IAppLogger logger) : IAgentRuntime
{
    private readonly IAppLogger _logger = logger.ForContext("AgentRuntime");

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
                var (updatedSession, response) = await CompleteWithOverflowRetryAsync(
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

    private async Task<(AgentSession Session, AgentModelResponse Response)> CompleteWithOverflowRetryAsync(
        AgentSession session,
        AgentTurnCallbacks? callbacks,
        AgentStreamAdapter streamAdapter,
        string assistantMessageId,
        IReadOnlyList<AgentModelMessage> modelMessages,
        IReadOnlyList<ToolDefinition> tools,
        FrozenSystemPrompt frozenPrompt,
        string environmentPrompt,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await modelClient.CompleteAsync(
                new AgentModelRequest(modelMessages, tools),
                token => PublishStreamEventsAsync(callbacks, streamAdapter.OnTextDelta(assistantMessageId, token)),
                token => PublishStreamEventsAsync(callbacks, streamAdapter.OnReasoningDelta(assistantMessageId, token)),
                delta => PublishStreamEventsAsync(callbacks, streamAdapter.OnToolCallDelta(assistantMessageId, delta)),
                cancellationToken);
            ObserveModelUsage(session, environmentPrompt, tools, response);
            return (session, response);
        }
        catch (HttpRequestException ex) when (IsContextLengthError(ex))
        {
            _logger.Warning("Context length exceeded for session {SessionId}; forcing compact and retrying once", session.Id);

            session = await RunPreCompletionPipelineAsync(
                session,
                callbacks,
                PreCompletionOptions.ForceCompact,
                environmentPrompt,
                tools,
                ContextPressureLevel.Overflow,
                cancellationToken);

            environmentPrompt = ResolveSystemPromptOrchestrator().BuildForReasoningIteration(
                frozenPrompt,
                session,
                tools);
            var retryMessages = BuildModelMessagesForSession(environmentPrompt, session.Messages);
            var response = await modelClient.CompleteAsync(
                new AgentModelRequest(retryMessages, tools),
                token => PublishStreamEventsAsync(callbacks, streamAdapter.OnTextDelta(assistantMessageId, token)),
                token => PublishStreamEventsAsync(callbacks, streamAdapter.OnReasoningDelta(assistantMessageId, token)),
                delta => PublishStreamEventsAsync(callbacks, streamAdapter.OnToolCallDelta(assistantMessageId, delta)),
                cancellationToken);
            ObserveModelUsage(session, environmentPrompt, tools, response);
            return (session, response);
        }
    }

    private async Task<AgentSession> InvokeToolAndPersistAsync(
        AgentSession session,
        string? parentMessageId,
        AgentToolCall toolCall,
        AgentStreamAdapter streamAdapter,
        AgentTurnCallbacks? callbacks,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        ToolResult result;
        try
        {
            result = await Task.Run(
                    () => ResolveToolRouter().InvokeAsync(new ToolInvocation(toolCall.Name, toolCall.Arguments), cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Tool {ToolName} threw; returning failure to the model", toolCall.Name);
            result = ToolResult.Failure("Tool invocation failed", ex.Message, sw.Elapsed);
        }

        sw.Stop();

        await storage.AppendToolCallLogAsync(
            session.Id,
            new SessionToolCallLogEntry(
                DateTimeOffset.UtcNow,
                toolCall.Id,
                toolCall.Name,
                toolCall.Arguments,
                result.Succeeded,
                result.Summary,
                result.Content,
                result.Error,
                sw.ElapsedMilliseconds),
            cancellationToken);

        var content = FormatToolResult(toolCall, result);
        content = await toolResultEvictor.EvictIfNeededAsync(
            session.Id,
            toolCall,
            result,
            content,
            cancellationToken);

        var toolMessage = ChatMessage.Create(MessageRole.Tool, content, parentMessageId);
        session = session.WithMessage(toolMessage);
        await PublishStreamEventsAsync(callbacks, streamAdapter.OnToolResult(toolMessage, toolCall));
        await PersistMessageAsync(session, toolMessage, cancellationToken);
        return session;
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
        if (settings.ContextCompaction.DynamicCompaction.Enabled)
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

    private void ObserveModelUsage(
        AgentSession session,
        string environmentPrompt,
        IReadOnlyList<ToolDefinition> tools,
        AgentModelResponse response)
    {
        if (response.Usage?.PromptTokens is not > 0)
        {
            return;
        }

        var multiplier = tokenEstimatorCalibrator.GetMultiplier(session.Id);
        var budget = ContextBudgetCalculator.Compute(
            environmentPrompt,
            tools,
            session.Messages,
            settings.ContextCompaction,
            settings.Model,
            multiplier);
        var estimatedPromptTokens = budget.FixedOverhead + budget.EstimatedHistory;
        tokenEstimatorCalibrator.Observe(session.Id, estimatedPromptTokens, response.Usage.PromptTokens);
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

    private static async Task PublishStreamEventsAsync(
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

    private static bool IsContextLengthError(Exception exception)
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
        BuildModelMessages(environmentPrompt, history, settings.ContextCompaction.IncludeReasoningInModelContext);

    internal static List<AgentModelMessage> BuildModelMessages(
        string environmentPrompt,
        IReadOnlyList<ChatMessage> history,
        bool includeReasoningInModelContext = false)
    {
        var messages = new List<AgentModelMessage>
        {
            new("system", environmentPrompt)
        };

        for (var index = 0; index < history.Count; index++)
        {
            var message = history[index];
            switch (message.Role)
            {
                case MessageRole.Compaction:
                    continue;
                case MessageRole.User:
                    messages.Add(new AgentModelMessage("user", BuildUserContent(message)));
                    break;
                case MessageRole.Assistant:
                    index = AppendAssistantModelMessages(messages, history, index, includeReasoningInModelContext);
                    break;
                case MessageRole.Tool:
                    messages.Add(new AgentModelMessage("user", FormatToolResultAsUserContent(message.Content)));
                    break;
                case MessageRole.Summary:
                    messages.Add(new AgentModelMessage("user", $"History summary: {message.Content}"));
                    break;
                case MessageRole.System:
                    messages.Add(new AgentModelMessage("user", message.Content));
                    break;
                default:
                    messages.Add(new AgentModelMessage("user", message.Content));
                    break;
            }
        }

        return messages;
    }

    private static int AppendAssistantModelMessages(
        List<AgentModelMessage> messages,
        IReadOnlyList<ChatMessage> history,
        int assistantIndex,
        bool includeReasoningInModelContext)
    {
        var message = history[assistantIndex];
        var reasoningContent = includeReasoningInModelContext ? message.ReasoningContent : null;
        var toolCalls = AssistantToolCallsCodec.Deserialize(message.ToolCallsJson);
        if (toolCalls is not { Count: > 0 })
        {
            messages.Add(new AgentModelMessage("assistant", message.Content, ReasoningContent: reasoningContent));
            return assistantIndex;
        }

        var scanIndex = assistantIndex + 1;
        var toolMessages = new List<ChatMessage>();
        while (scanIndex < history.Count)
        {
            switch (history[scanIndex].Role)
            {
                case MessageRole.Tool:
                    toolMessages.Add(history[scanIndex]);
                    scanIndex++;
                    break;
                case MessageRole.Compaction:
                    scanIndex++;
                    break;
                default:
                    goto DoneScanning;
            }
        }

        DoneScanning:
        var toolByCallId = new Dictionary<string, ChatMessage>(StringComparer.Ordinal);
        foreach (var toolMessage in toolMessages)
        {
            var toolCallId = ExtractToolCallId(toolMessage.Content);
            if (!string.IsNullOrWhiteSpace(toolCallId))
            {
                toolByCallId.TryAdd(toolCallId, toolMessage);
            }
        }

        messages.Add(new AgentModelMessage("assistant", message.Content, ToolCalls: toolCalls, ReasoningContent: reasoningContent));
        foreach (var toolCall in toolCalls)
        {
            var content = toolByCallId.TryGetValue(toolCall.Id, out var toolMessage)
                ? toolMessage.Content
                : "Tool did not run or the result was not recorded.";
            messages.Add(new AgentModelMessage("tool", content, toolCall.Id));
        }

        var consumed = new HashSet<string>(toolCalls.Select(call => call.Id), StringComparer.Ordinal);
        foreach (var toolMessage in toolMessages)
        {
            var toolCallId = ExtractToolCallId(toolMessage.Content);
            if (toolCallId is not null && consumed.Contains(toolCallId))
            {
                continue;
            }

            messages.Add(new AgentModelMessage("user", FormatToolResultAsUserContent(toolMessage.Content)));
        }

        return scanIndex - 1;
    }

    private static string FormatToolResultAsUserContent(string content) =>
        string.Join(Environment.NewLine, "[Tool output]", content);

    private static object BuildUserContent(ChatMessage message)
    {
        if (message.ImageAttachments is not { Count: > 0 })
        {
            return message.Content;
        }

        var parts = new List<object>
        {
            new Dictionary<string, object?>
            {
                ["type"] = "text",
                ["text"] = message.Content
            }
        };

        foreach (var image in message.ImageAttachments)
        {
            var dataUrl = ImageAttachmentDataUrlResolver.ResolveDataUrl(image);
            if (string.IsNullOrWhiteSpace(dataUrl))
            {
                continue;
            }

            parts.Add(new Dictionary<string, object?>
            {
                ["type"] = "image_url",
                ["image_url"] = new Dictionary<string, object?>
                {
                    ["url"] = dataUrl
                }
            });
        }

        return parts;
    }

    public static string FormatToolResult(AgentToolCall call, ToolResult result)
    {
        var status = result.Succeeded ? "succeeded" : "failed";
        return string.Join(Environment.NewLine, new[]
        {
            $"ToolCallId: {call.Id}",
            $"Tool `{call.Name}` {status}.",
            "",
            $"Arguments: {FormatArguments(call.Arguments)}",
            $"Summary: {result.Summary}",
            "",
            result.Content ?? result.Error ?? string.Empty
        });
    }

    private static string FormatArguments(IReadOnlyDictionary<string, string> arguments)
    {
        return arguments.Count == 0
            ? "(none)"
            : string.Join(Environment.NewLine, arguments.Select(argument => $"{argument.Key}={argument.Value}"));
    }

    private static string? ExtractToolCallId(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        foreach (var line in content.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            const string prefix = "ToolCallId:";
            if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var value = line[prefix.Length..].Trim();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }

        return null;
    }

    private static bool ShouldListWorkspaceFiles(string userInput)
    {
        var input = userInput.Trim();
        return ContainsAny(input, "有哪些文件", "什么文件", "文件列表", "目录下", "目录里", "工作区文件", "list files", "what files", "which files");
    }

    private static bool ContainsAny(string value, params string[] terms)
    {
        return terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private IToolRouter ResolveToolRouter() => AmbientToolRouterScope.CurrentRouter ?? toolRouter;

    private ISystemPromptOrchestrator ResolveSystemPromptOrchestrator() =>
        AmbientSystemPromptOrchestratorScope.CurrentOrchestrator ?? systemPromptOrchestrator;
}
public sealed class AgentOrchestrator(IAgentRuntime agentRuntime) : IAgentOrchestrator
{
    public Task<AgentSession> SendAsync(
        AgentSession session,
        string userInput,
        IReadOnlyList<ImageAttachment>? imageAttachments = null,
        AgentTurnCallbacks? callbacks = null,
        CancellationToken cancellationToken = default) =>
        agentRuntime.SendAsync(session, userInput, imageAttachments, callbacks, cancellationToken);
}
