using Athlon.Agent.Core.BehaviorReport;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.Prompt;
using Athlon.Agent.Core.Streaming;
using Athlon.Agent.Core.Events;
using Athlon.Agent.Core.Middleware;
using Athlon.Agent.Core.SubAgents;
using Athlon.Agent.Core.RuntimeDiagnostics;

namespace Athlon.Agent.Core;

/// <summary>Transcript persistence, stream publishing and turn completion.</summary>
public sealed partial class AgentRuntime
{
    private async Task PersistMessageAsync(AgentSession session, ChatMessage message, CancellationToken cancellationToken)
    {
        await _transcript.AppendAsync(session.Id, message, cancellationToken).ConfigureAwait(false);
        await _transcript.MarkSessionDirtyAsync(session, cancellationToken).ConfigureAwait(false);
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

    private async Task RecordTrainingDataAsync(AgentSession session, CancellationToken cancellationToken)
    {
        var collector = ResolveTrainingDataCollector();
        if (collector is null)
            return;

        try
        {
            await collector.RecordTurnAsync(session, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warning("Training data recording failed: {Error}", ex.Message);
        }
    }

    private async Task PublishTurnFinishedAsync(
        AgentTurnCallbacks? callbacks,
        AgentRunContext runContext,
        AgentSession session,
        TurnOutcomeKind outcome,
        CancellationToken cancellationToken)
    {
        if (outcome == TurnOutcomeKind.MaxToolRoundsReached)
        {
            _eventManager.Record(
                BehaviorEventIds.Turn,
                BehaviorEventTypes.Event,
                BehaviorEventIds.Turn,
                new Dictionary<string, object?>
                {
                    ["session_id"] = session.Id,
                    ["run_id"] = runContext.RunId,
                    ["outcome"] = "max_tool_rounds"
                });
        }

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
