using System.Diagnostics;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.Streaming;

namespace Athlon.Agent.Core;

internal sealed class ToolInvocationPipeline(
    IFileStorageService storage,
    IToolResultEvictor toolResultEvictor,
    Func<IToolRouter> resolveToolRouter,
    IAppLogger logger)
{
    private readonly IAppLogger _logger = logger.ForContext("ToolInvocationPipeline");

    public async Task<AgentSession> InvokeAndPersistAsync(
        AgentSession session,
        string? parentMessageId,
        AgentToolCall toolCall,
        AgentStreamAdapter streamAdapter,
        AgentTurnCallbacks? callbacks,
        Func<AgentSession, ChatMessage, CancellationToken, Task> persistMessageAsync,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        ToolResult result;
        try
        {
            result = await Task.Run(
                    () => resolveToolRouter().InvokeAsync(new ToolInvocation(toolCall.Name, toolCall.Arguments), cancellationToken),
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

        var content = ModelMessageBuilder.FormatToolResult(toolCall, result);
        content = await toolResultEvictor.EvictIfNeededAsync(
            session.Id,
            toolCall,
            result,
            content,
            cancellationToken);

        var toolMessage = ChatMessage.Create(MessageRole.Tool, content, parentMessageId);
        session = session.WithMessage(toolMessage);
        await AgentRuntime.PublishStreamEventsAsync(callbacks, streamAdapter.OnToolResult(toolMessage, toolCall));
        await persistMessageAsync(session, toolMessage, cancellationToken);
        return session;
    }
}
