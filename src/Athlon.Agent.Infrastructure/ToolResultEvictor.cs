using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.RuntimeDiagnostics;

namespace Athlon.Agent.Infrastructure;

public sealed class ToolResultEvictor(
    AppSettings settings,
    IFileStorageService storage,
    IAgentRunContextAccessor? runContextAccessor = null,
    IRuntimeDiagnosticEventSink? runtimeDiagnosticEventSink = null) : IToolResultEvictor
{
    public async Task<string> EvictIfNeededAsync(
        string sessionId,
        AgentToolCall toolCall,
        ToolResult result,
        string formattedToolContent,
        CancellationToken cancellationToken = default)
    {
        var cfg = settings.ContextCompaction.ToolResultEviction;
        if (!cfg.Enabled)
        {
            return formattedToolContent;
        }

        if (cfg.ExcludedToolNames.Any(name =>
                string.Equals(name, toolCall.Name, StringComparison.OrdinalIgnoreCase)))
        {
            return formattedToolContent;
        }

        var rawContent = result.Content ?? string.Empty;
        if (rawContent.Length <= cfg.MaxResultChars)
        {
            return formattedToolContent;
        }

        string path;
        try
        {
            path = await storage.SaveEvictedToolResultAsync(sessionId, toolCall.Id, rawContent, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await EnqueueDiagnosticAsync(
                sessionId,
                toolCall.Id,
                RuntimeDiagnosticPhase.Persist,
                "storage.persist_failed",
                RuntimeDiagnosticSeverity.Error,
                RuntimeDiagnosticErrorCodes.StoragePersistFailed,
                $"SaveEvictedToolResult failed: {ex.Message}").ConfigureAwait(false);
            return formattedToolContent;
        }

        var preview = BuildPreview(rawContent, cfg.PreviewChars);
        var placeholder = new StringBuilder()
            .AppendLine($"[Tool result evicted - {rawContent.Length} chars]")
            .AppendLine($"Archived at: {path}")
            .AppendLine("Preview:")
            .Append(preview)
            .ToString();

        await EnqueueDiagnosticAsync(
            sessionId,
            toolCall.Id,
            RuntimeDiagnosticPhase.Persist,
            "tool.output_evicted",
            RuntimeDiagnosticSeverity.Warning,
            RuntimeDiagnosticErrorCodes.ToolOutputEvicted,
            $"Evicted oversized tool result ({rawContent.Length} chars) for {toolCall.Name}.").ConfigureAwait(false);

        return AgentRuntime.FormatToolResult(
            toolCall,
            ToolResult.Success(result.Summary, placeholder));
    }

    private async Task EnqueueDiagnosticAsync(
        string sessionId,
        string toolCallId,
        RuntimeDiagnosticPhase phase,
        string eventType,
        RuntimeDiagnosticSeverity severity,
        string errorCode,
        string message)
    {
        if (runtimeDiagnosticEventSink is not { } sink)
        {
            return;
        }

        var context = runContextAccessor?.Current;
        var evt = new RuntimeDiagnosticEvent(
            eventId: "",
            ts: default,
            sequence: 0,
            sessionId: sessionId,
            runId: context?.RunId ?? sessionId,
            turnId: null,
            attemptId: toolCallId,
            parentAttemptId: null,
            toolCallId: toolCallId,
            messageId: null,
            component: RuntimeDiagnosticComponent.Tool,
            phase: phase,
            eventType: eventType,
            severity: severity,
            errorCode: errorCode,
            message: message);
        await sink.EnqueueAsync(evt, CancellationToken.None).ConfigureAwait(false);
    }

    private static string BuildPreview(string content, int previewChars)
    {
        if (content.Length <= previewChars * 2)
        {
            return content;
        }

        var head = content[..previewChars];
        var tail = content[^previewChars..];
        return head + "\n...\n" + tail;
    }
}
