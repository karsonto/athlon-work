using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json.Serialization;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.Prompt;

namespace Athlon.Agent.Core;

public sealed class AgentRuntime(
    IAgentModelClient modelClient,
    IFileStorageService storage,
    IToolRouter toolRouter,
    ISystemPromptOrchestrator systemPromptOrchestrator,
    IPreCompletionPipeline preCompletionPipeline,
    IToolResultEvictor toolResultEvictor,
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

            var tools = toolRouter.ListTools();
            var frozenPrompt = systemPromptOrchestrator.PrepareForTurn(session, tools);
            var environmentPrompt = systemPromptOrchestrator.BuildForReasoningIteration(frozenPrompt, session, tools);
            var modelMessages = BuildModelMessages(environmentPrompt, session.Messages);

            if (ShouldListWorkspaceFiles(userInput) && tools.Any(tool => string.Equals(tool.Name, "file_list", StringComparison.OrdinalIgnoreCase)))
            {
                var toolCall = new AgentToolCall(Guid.NewGuid().ToString("N"), "file_list", new Dictionary<string, string>());
                await NotifyToolStartedAsync(callbacks, toolCall);
                session = await InvokeToolAndPersistAsync(session, userMessage.Id, toolCall, callbacks, cancellationToken);
            }

            while (true)
            {
                session = await RunPreCompletionPipelineAsync(
                    session,
                    callbacks,
                    PreCompletionOptions.AgentLoop,
                    cancellationToken);
                environmentPrompt = systemPromptOrchestrator.BuildForReasoningIteration(frozenPrompt, session, tools);
                modelMessages = BuildModelMessages(environmentPrompt, session.Messages);

                var (updatedSession, response) = await CompleteWithOverflowRetryAsync(
                    session,
                    callbacks,
                    modelMessages,
                    tools,
                    frozenPrompt,
                    environmentPrompt,
                    cancellationToken);
                session = updatedSession;

                if (response.ToolCalls.Count == 0)
                {
                    var assistant = ChatMessage.Create(
                        MessageRole.Assistant,
                        response.Content,
                        userMessage.Id,
                        reasoningContent: response.ReasoningContent);
                    session = session.WithMessage(assistant);
                    await NotifyMessageAsync(callbacks, assistant);
                    await PersistMessageAsync(session, assistant, cancellationToken);
                    _logger.Information("Saved session {SessionId} with {MessageCount} messages", session.Id, session.Messages.Count);
                    return session;
                }

                var assistantWithToolCalls = ChatMessage.Create(
                    MessageRole.Assistant,
                    response.Content,
                    userMessage.Id,
                    response.ToolCalls,
                    response.ReasoningContent);
                session = session.WithMessage(assistantWithToolCalls);
                await PersistMessageAsync(session, assistantWithToolCalls, cancellationToken);

                environmentPrompt = systemPromptOrchestrator.BuildForReasoningIteration(frozenPrompt, session, tools);
                modelMessages = BuildModelMessages(environmentPrompt, session.Messages);
                foreach (var toolCall in response.ToolCalls)
                {
                    await NotifyToolStartedAsync(callbacks, toolCall);
                    session = await InvokeToolAndPersistAsync(session, userMessage.Id, toolCall, callbacks, cancellationToken);
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
                callbacks?.OnAssistantTextDelta,
                callbacks?.OnAssistantReasoningDelta,
                callbacks?.OnAssistantToolCallDelta,
                cancellationToken);
            return (session, response);
        }
        catch (HttpRequestException ex) when (IsContextLengthError(ex))
        {
            _logger.Warning("Context length exceeded for session {SessionId}; forcing compact and retrying once", session.Id);

            session = await RunPreCompletionPipelineAsync(
                session,
                callbacks,
                PreCompletionOptions.ForceCompact,
                cancellationToken);

            environmentPrompt = systemPromptOrchestrator.BuildForReasoningIteration(
                frozenPrompt,
                session,
                tools);
            var retryMessages = BuildModelMessages(environmentPrompt, session.Messages);
            var response = await modelClient.CompleteAsync(
                new AgentModelRequest(retryMessages, tools),
                callbacks?.OnAssistantTextDelta,
                callbacks?.OnAssistantReasoningDelta,
                callbacks?.OnAssistantToolCallDelta,
                cancellationToken);
            return (session, response);
        }
    }

    private async Task<AgentSession> InvokeToolAndPersistAsync(
        AgentSession session,
        string? parentMessageId,
        AgentToolCall toolCall,
        AgentTurnCallbacks? callbacks,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var result = await Task.Run(
            () => toolRouter.InvokeAsync(new ToolInvocation(toolCall.Name, toolCall.Arguments), cancellationToken),
            cancellationToken);
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
        await NotifyMessageAsync(callbacks, toolMessage);
        await PersistMessageAsync(session, toolMessage, cancellationToken);
        return session;
    }

    private async Task<AgentSession> RunPreCompletionPipelineAsync(
        AgentSession session,
        AgentTurnCallbacks? callbacks,
        PreCompletionOptions options,
        CancellationToken cancellationToken)
    {
        var messageIdsBefore = session.Messages.Select(message => message.Id).ToHashSet(StringComparer.Ordinal);
        session = await preCompletionPipeline.RunAsync(session, options, cancellationToken);
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
            await NotifyMessageAsync(callbacks, message);
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

    private static async Task NotifyMessageAsync(AgentTurnCallbacks? callbacks, ChatMessage message)
    {
        if (callbacks?.OnMessage is not null)
        {
            await callbacks.OnMessage(message);
        }
    }

    private static async Task NotifyToolStartedAsync(AgentTurnCallbacks? callbacks, AgentToolCall toolCall)
    {
        if (callbacks?.OnToolStarted is not null)
        {
            await callbacks.OnToolStarted(toolCall);
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

    internal static List<AgentModelMessage> BuildModelMessages(string environmentPrompt, IReadOnlyList<ChatMessage> history)
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
                    index = AppendAssistantModelMessages(messages, history, index);
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
        int assistantIndex)
    {
        var message = history[assistantIndex];
        var toolCalls = AssistantToolCallsCodec.Deserialize(message.ToolCallsJson);
        if (toolCalls is not { Count: > 0 })
        {
            messages.Add(new AgentModelMessage("assistant", message.Content, ReasoningContent: message.ReasoningContent));
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

        messages.Add(new AgentModelMessage("assistant", message.Content, ToolCalls: toolCalls, ReasoningContent: message.ReasoningContent));
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
            parts.Add(new Dictionary<string, object?>
            {
                ["type"] = "image_url",
                ["image_url"] = new Dictionary<string, object?>
                {
                    ["url"] = image.DataUrl
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
