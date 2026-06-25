using System.Diagnostics;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.Streaming;

namespace Athlon.Agent.Core;

internal sealed record ToolInvocationOutcome(ToolResult Result, long ElapsedMilliseconds);

internal sealed class ToolInvocationPipeline(
    IFileStorageService storage,
    IToolResultEvictor toolResultEvictor,
    Func<IToolRouter> resolveToolRouter,
    IAppLogger logger)
{
    private readonly IAppLogger _logger = logger.ForContext("ToolInvocationPipeline");

    public async Task<ToolInvocationOutcome> InvokeCoreAsync(
        string sessionId,
        AgentToolCall toolCall,
        AgentTurnCallbacks? callbacks,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        ToolResult result;
        try
        {
            using var outputStream = new AmbientToolOutputStream(callbacks, toolCall.Id);
            result = await resolveToolRouter()
                .InvokeAsync(new ToolInvocation(toolCall.Name, toolCall.Arguments), cancellationToken)
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
            sessionId,
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
            cancellationToken).ConfigureAwait(false);

        return new ToolInvocationOutcome(result, sw.ElapsedMilliseconds);
    }

    public async Task<AgentSession> PersistToolResultAsync(
        AgentSession session,
        string? parentMessageId,
        AgentToolCall toolCall,
        ToolResult result,
        AgentStreamAdapter streamAdapter,
        AgentTurnCallbacks? callbacks,
        Func<AgentSession, ChatMessage, CancellationToken, Task> persistMessageAsync,
        CancellationToken cancellationToken)
    {
        var content = ModelMessageBuilder.FormatToolResult(toolCall, result);
        content = await toolResultEvictor.EvictIfNeededAsync(
            session.Id,
            toolCall,
            result,
            content,
            cancellationToken).ConfigureAwait(false);

        var toolMessage = ChatMessage.Create(MessageRole.Tool, content, parentMessageId);
        session = session.WithMessage(toolMessage);
        await AgentRuntime.PublishStreamEventsAsync(callbacks, streamAdapter.OnToolResult(toolMessage, toolCall));
        await persistMessageAsync(session, toolMessage, cancellationToken).ConfigureAwait(false);
        return session;
    }

    public async Task<AgentSession> InvokeAndPersistAsync(
        AgentSession session,
        string? parentMessageId,
        AgentToolCall toolCall,
        AgentStreamAdapter streamAdapter,
        AgentTurnCallbacks? callbacks,
        Func<AgentSession, ChatMessage, CancellationToken, Task> persistMessageAsync,
        CancellationToken cancellationToken)
    {
        var outcome = await InvokeCoreAsync(session.Id, toolCall, callbacks, cancellationToken).ConfigureAwait(false);
        return await PersistToolResultAsync(
            session,
            parentMessageId,
            toolCall,
            outcome.Result,
            streamAdapter,
            callbacks,
            persistMessageAsync,
            cancellationToken).ConfigureAwait(false);
    }
}
