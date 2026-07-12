using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.Streaming;

namespace Athlon.Agent.Core;

internal sealed record ToolInvocationOutcome(ToolResult Result, long ElapsedMilliseconds);

internal sealed class ToolInvocationPipeline(
    IFileStorageService storage,
    IToolResultEvictor toolResultEvictor,
    Func<IToolRouter> resolveToolRouter,
    IAgentRunContextAccessor runContextAccessor,
    Func<bool> isApprovalEnabled,
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
            result = await InvokeValidatedAsync(toolCall, callbacks, cancellationToken).ConfigureAwait(false);
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

        var definition = resolveToolRouter().ListTools().FirstOrDefault(
            tool => string.Equals(tool.Name, toolCall.Name, StringComparison.OrdinalIgnoreCase));
        var context = runContextAccessor.Current;
        await storage.AppendAttemptEventAsync(
            sessionId,
            new AgentAttemptEvent(
                DateTimeOffset.UtcNow,
                toolCall.Id,
                sessionId,
                context?.RunId ?? sessionId,
                AgentAttemptKind.Tool,
                context?.ParentSessionId is null ? ModelCallPurpose.Chat : ModelCallPurpose.SubAgent,
                toolCall.Name,
                definition is null ? null : ToolCatalogFingerprint.Compute([definition]),
                null,
                0,
                0,
                result.Succeeded ? "success" : "failure",
                ExtractErrorCode(result.Error),
                sw.ElapsedMilliseconds,
                InputFingerprint: ComputeFingerprint(toolCall.Arguments.ToJsonString())),
            cancellationToken).ConfigureAwait(false);

        return new ToolInvocationOutcome(result, sw.ElapsedMilliseconds);
    }

    private static string ComputeFingerprint(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];

    private static string? ExtractErrorCode(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(error);
            return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
        }
        catch (JsonException)
        {
            return "tool.execution_failed";
        }
    }

    private async Task<ToolResult> InvokeValidatedAsync(
        AgentToolCall toolCall,
        AgentTurnCallbacks? callbacks,
        CancellationToken cancellationToken)
    {
        var router = resolveToolRouter();
        var definition = router.ListTools().FirstOrDefault(
            tool => string.Equals(tool.Name, toolCall.Name, StringComparison.OrdinalIgnoreCase));
        if (definition is null)
        {
            return await router
                .InvokeAsync(new ToolInvocation(toolCall.Name, toolCall.Arguments), cancellationToken)
                .ConfigureAwait(false);
        }

        var validationError = ToolInvocationValidator.Validate(definition.ParametersSchema, toolCall.Arguments);
        if (validationError is not null)
        {
            return ToolInvocationErrors.Failure("Invalid tool arguments", validationError);
        }

        var approvalDecision = ToolApprovalDecision.None;
        PendingToolApproval? pendingApproval = null;
        if (ToolInvocationPolicyEnforcer.RequiresApproval(definition)
            && definition.InvocationPolicy != ToolInvocationPolicy.Deny)
        {
            if (!isApprovalEnabled())
            {
                approvalDecision = ToolApprovalDecision.Approved;
            }
            else
            {
                pendingApproval = ToolInvocationPolicyEnforcer.TryCreatePendingApproval(toolCall, definition);
                if (callbacks?.OnToolApprovalRequested is null)
                {
                    approvalDecision = ToolApprovalDecision.Pending;
                }
                else
                {
                    try
                    {
                        approvalDecision = await callbacks.OnToolApprovalRequested(
                            pendingApproval!,
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        return ToolInvocationErrors.Failure(
                            "Tool approval unavailable",
                            new ToolInvocationError(
                                "policy.approval_callback_failed",
                                "$",
                                "an explicit approval decision",
                                ex.GetType().Name,
                                "Retry after the approval UI is available; do not execute the tool implicitly."));
                    }
                }
            }
        }

        var blocked = ToolInvocationPolicyEnforcer.TryBlockInvocation(
            definition,
            approvalDecision,
            pendingApproval);
        if (blocked is not null)
        {
            return blocked;
        }

        return await router
            .InvokeAsync(
                new ToolInvocation(
                    toolCall.Name,
                    toolCall.Arguments,
                    ApprovalDecision: approvalDecision),
                cancellationToken)
            .ConfigureAwait(false);
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
        await AgentRuntime.PublishStreamEventsAsync(callbacks, streamAdapter.OnToolResult(toolMessage, toolCall)).ConfigureAwait(false);
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
