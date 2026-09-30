using Athlon.Agent.Core.BehaviorReport;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.Prompt;
using Athlon.Agent.Core.Streaming;
using Athlon.Agent.Core.Events;
using Athlon.Agent.Core.Middleware;
using Athlon.Agent.Core.SubAgents;
using Athlon.Agent.Core.RuntimeDiagnostics;

namespace Athlon.Agent.Core;

/// <summary>Tool invocation, parallel batches and catalog drift.</summary>
public sealed partial class AgentRuntime
{
    private async Task<(AgentSession Session, bool EndsTurn)> InvokeToolAndPersistAsync(
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
            var toolMessage = ChatMessage.CreateWithId(
                ChatMessage.ToolResultMessageId(toolCall.Id),
                MessageRole.Tool,
                content,
                parentMessageId);
            session = session.WithUpsertedMessage(toolMessage);
            await PublishStreamEventsAsync(callbacks, streamAdapter.OnToolResult(toolMessage, toolCall)).ConfigureAwait(false);
            await PersistMessageAsync(session, toolMessage, cancellationToken).ConfigureAwait(false);
            invocation.Session = session;
            await turnPipeline.OnAfterToolInvokeAsync(invocation, toolCall, cancellationToken).ConfigureAwait(false);
            await NotifySessionUpdatedAsync(callbacks, session).ConfigureAwait(false);
            return (session, false);
        }

        ToolResult result;
        (session, result) = await _toolPipeline.InvokeAndPersistAsync(
            session,
            parentMessageId,
            toolCall,
            streamAdapter,
            callbacks,
            PersistMessageAsync,
            cancellationToken).ConfigureAwait(false);
        invocation.Session = session;
        await turnPipeline.OnAfterToolInvokeAsync(invocation, toolCall, cancellationToken).ConfigureAwait(false);
        await NotifySessionUpdatedAsync(callbacks, session).ConfigureAwait(false);
        return (session, result is { Succeeded: true, EndsTurn: true });
    }

    private async Task<(AgentSession Session, bool EndsTurn)> InvokeParallelToolBatchAsync(
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

        foreach (var item in pending)
        {
            await turnPipeline.OnBeforeToolInvokeAsync(invocation, item.Call, cancellationToken).ConfigureAwait(false);
        }

        var session = invocation.Session;
        foreach (var item in pending)
        {
            session = await _toolPipeline.PersistRunningToolPlaceholderAsync(
                session,
                parentMessageId,
                item.Call,
                PersistMessageAsync,
                cancellationToken).ConfigureAwait(false);
        }

        invocation.Session = session;

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

        var endsTurn = false;
        for (var index = 0; index < toolCalls.Count; index++)
        {
            var toolCall = toolCalls[index];
            var result = results[index]
                ?? ToolResult.Failure("Tool invocation failed", "No result was produced for this tool call.");
            endsTurn |= result is { Succeeded: true, EndsTurn: true };
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

        await NotifySessionUpdatedAsync(invocation.Callbacks, session).ConfigureAwait(false);
        return (session, endsTurn);
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
}
