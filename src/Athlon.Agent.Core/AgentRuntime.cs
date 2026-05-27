using System.Diagnostics;
using System.Text;
using System.Text.Json.Serialization;
using Athlon.Agent.Core.Compaction;

namespace Athlon.Agent.Core;

public sealed class AgentRuntime(
    IAgentModelClient modelClient,
    IFileStorageService storage,
    IToolRouter toolRouter,
    IAgentEnvironmentPromptBuilder promptBuilder,
    IPreCompletionPipeline preCompletionPipeline,
    IAutoCompactService autoCompactService,
    IActiveAgentSessionContext activeSessionContext,
    IAppLogger logger) : IAgentRuntime
{
    private readonly IAppLogger _logger = logger.ForContext("AgentRuntime");

    public async Task<AgentSession> SendAsync(
        AgentSession session,
        string userInput,
        AgentTurnCallbacks? callbacks = null,
        CancellationToken cancellationToken = default)
    {
        activeSessionContext.SetSession(session.Id);
        try
        {
            return await SendAsyncCore(session, userInput, callbacks, cancellationToken);
        }
        finally
        {
            activeSessionContext.SetSession(null);
        }
    }

    private Task<AgentSession> SendAsyncCore(
        AgentSession session,
        string userInput,
        AgentTurnCallbacks? callbacks,
        CancellationToken cancellationToken) =>
        SendAsyncTurnAsync(session, userInput, callbacks, cancellationToken);

    private async Task<AgentSession> SendAsyncTurnAsync(
        AgentSession session,
        string userInput,
        AgentTurnCallbacks? callbacks,
        CancellationToken cancellationToken)
    {
        try
        {
            var userMessage = ChatMessage.Create(MessageRole.User, userInput, session.Messages.LastOrDefault()?.Id);
            session = session.WithMessage(userMessage);
            await PersistMessageAsync(session, userMessage, cancellationToken);

            var tools = toolRouter.ListTools();
            var environmentPrompt = promptBuilder.Build(session, tools);
            var modelMessages = BuildModelMessages(environmentPrompt, session.Messages);

            if (ShouldListWorkspaceFiles(userInput) && tools.Any(tool => string.Equals(tool.Name, "file_list", StringComparison.OrdinalIgnoreCase)))
            {
                var toolCall = new AgentToolCall(Guid.NewGuid().ToString("N"), "file_list", new Dictionary<string, string>());
                await NotifyToolStartedAsync(callbacks, toolCall);
                session = await InvokeToolAndPersistAsync(session, userMessage.Id, toolCall, callbacks, cancellationToken);
            }

            while (true)
            {
                session = await preCompletionPipeline.RunAsync(session, cancellationToken);
                modelMessages = BuildModelMessages(environmentPrompt, session.Messages);

                var response = await modelClient.CompleteAsync(
                    new AgentModelRequest(modelMessages, tools),
                    callbacks?.OnAssistantTextDelta,
                    cancellationToken);
                if (response.ToolCalls.Count == 0)
                {
                    var assistant = ChatMessage.Create(MessageRole.Assistant, response.Content, userMessage.Id);
                    session = session.WithMessage(assistant);
                    await NotifyMessageAsync(callbacks, assistant);
                    await PersistMessageAsync(session, assistant, cancellationToken);
                    _logger.Information("Saved session {SessionId} with {MessageCount} messages", session.Id, session.Messages.Count);
                    return session;
                }

                modelMessages.Add(new AgentModelMessage("assistant", response.Content, ToolCalls: response.ToolCalls));
                foreach (var toolCall in response.ToolCalls)
                {
                    await NotifyToolStartedAsync(callbacks, toolCall);

                    if (string.Equals(toolCall.Name, CompressTool.ToolName, StringComparison.OrdinalIgnoreCase))
                    {
                        session = await autoCompactService.CompactAsync(session, cancellationToken);
                        var compressedNote = ChatMessage.Create(
                            MessageRole.Assistant,
                            "Context compressed. Continue from the summary in the latest user message.",
                            userMessage.Id);
                        session = session.WithMessage(compressedNote);
                        await NotifyMessageAsync(callbacks, compressedNote);
                        await PersistMessageAsync(session, compressedNote, cancellationToken);
                        return session;
                    }

                    session = await InvokeToolAndPersistAsync(session, userMessage.Id, toolCall, callbacks, cancellationToken);
                    modelMessages.Add(new AgentModelMessage("tool", session.Messages[^1].Content, toolCall.Id));
                }
            }
        }
        catch (OperationCanceledException)
        {
            await storage.SaveSessionAsync(session, CancellationToken.None);
            throw;
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
        // Force tool execution onto the thread pool so synchronous/heavy tool implementations
        // do not run inline with the caller's context (e.g., UI-originated workflow).
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
        var toolMessage = ChatMessage.Create(MessageRole.Tool, content, parentMessageId);
        session = session.WithMessage(toolMessage);
        await NotifyMessageAsync(callbacks, toolMessage);
        await PersistMessageAsync(session, toolMessage, cancellationToken);
        return session;
    }

    private async Task PersistMessageAsync(AgentSession session, ChatMessage message, CancellationToken cancellationToken)
    {
        await storage.AppendConversationMessageAsync(session.Id, message, cancellationToken);
        await storage.SaveSessionAsync(session, cancellationToken);
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

    private static List<AgentModelMessage> BuildModelMessages(string environmentPrompt, IReadOnlyList<ChatMessage> history)
    {
        var messages = new List<AgentModelMessage>
        {
            new("system", environmentPrompt)
        };

        foreach (var message in history)
        {
            messages.Add(message.Role switch
            {
                MessageRole.User => new AgentModelMessage("user", message.Content),
                MessageRole.Assistant => new AgentModelMessage("assistant", message.Content),
                MessageRole.Tool => new AgentModelMessage("tool", message.Content, ExtractToolCallId(message.Content)),
                MessageRole.Summary => new AgentModelMessage("user", $"History summary: {message.Content}"),
                _ => new AgentModelMessage("user", message.Content)
            });
        }

        return messages;
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
            : string.Join("; ", arguments.Select(argument => $"{argument.Key}={argument.Value}"));
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
        AgentTurnCallbacks? callbacks = null,
        CancellationToken cancellationToken = default) =>
        agentRuntime.SendAsync(session, userInput, callbacks, cancellationToken);
}
