namespace Athlon.Agent.Core.RuntimeDiagnostics;

/// <summary>
/// Frozen P0/P1 runtime failure points used by diagnostics coverage tests.
/// Keep this list in sync with production instrumentation.
/// </summary>
public static class RuntimeDiagnosticFailureMatrix
{
    public sealed record Entry(
        string FailurePointId,
        string Component,
        string Phase,
        string ErrorCode,
        string Severity);

    public static IReadOnlyList<Entry> P0P1Entries { get; } =
    [
        new("model.request.failed", "Model", "Request", RuntimeDiagnosticErrorCodes.ModelRequestFailed, "Error"),
        new("model.response.json_invalid", "Model", "Response", RuntimeDiagnosticErrorCodes.ModelResponseJsonInvalid, "Error"),
        new("model.tool_call.json_invalid", "Model", "Response", RuntimeDiagnosticErrorCodes.ModelToolCallJsonInvalid, "Error"),
        new("model.context_length.exceeded", "Model", "Request", RuntimeDiagnosticErrorCodes.ModelContextLengthExceeded, "Error"),
        new("model.streaming.idle_timeout", "Model", "Response", RuntimeDiagnosticErrorCodes.ModelStreamingIdleTimeout, "Error"),
        new("model.streaming.first_token_timeout", "Model", "Response", RuntimeDiagnosticErrorCodes.ModelStreamingFirstTokenTimeout, "Error"),
        new("model.streaming.interrupted", "Model", "Response", RuntimeDiagnosticErrorCodes.ModelStreamingInterrupted, "Warning"),

        new("tool.invoke.failed", "Tool", "Invoke", RuntimeDiagnosticErrorCodes.ToolInvokeFailed, "Error"),
        new("tool.output.evicted", "Tool", "Persist", RuntimeDiagnosticErrorCodes.ToolOutputEvicted, "Warning"),

        new("storage.persist.failed", "Storage", "Persist", RuntimeDiagnosticErrorCodes.StoragePersistFailed, "Error"),
        new("storage.load.failed", "Storage", "Load", RuntimeDiagnosticErrorCodes.StorageLoadFailed, "Error"),

        new("compaction.summary.failed", "Compaction", "Persist", RuntimeDiagnosticErrorCodes.CompactionSummaryFailed, "Error"),
        new("compaction.retry.skipped", "Compaction", "Persist", RuntimeDiagnosticErrorCodes.CompactionRetrySkipped, "Warning"),
        new("compaction.middle_cut.applied", "Compaction", "Compact", RuntimeDiagnosticErrorCodes.CompactionMiddleCutApplied, "Warning"),

        new("ui.webview.init_failed", "UiWebview", "Initialize", RuntimeDiagnosticErrorCodes.UiWebviewInitFailed, "Error"),
        new("ui.webview.script_failed", "UiWebview", "Invoke", RuntimeDiagnosticErrorCodes.UiWebviewScriptFailed, "Error"),
        new("ui.session_switch.surface_mismatch", "UiSessionSwitch", "Switch", RuntimeDiagnosticErrorCodes.UiSessionSwitchSurfaceMismatch, "Warning"),

        new("mcp.connect.failed", "Mcp", "Request", RuntimeDiagnosticErrorCodes.McpConnectFailed, "Error"),
        new("mcp.list_tools.failed", "Mcp", "Request", RuntimeDiagnosticErrorCodes.McpListToolsFailed, "Error"),
        new("mcp.tool.invoke.failed", "Mcp", "Invoke", RuntimeDiagnosticErrorCodes.McpToolInvokeFailed, "Error"),

        new("ssh.connect.failed", "Ssh", "Request", RuntimeDiagnosticErrorCodes.SshConnectFailed, "Error"),
        new("subagent.run.failed", "Subagent", "Invoke", RuntimeDiagnosticErrorCodes.SubagentRunFailed, "Error"),
        new("behavior.upload.failed", "Behavior", "Persist", RuntimeDiagnosticErrorCodes.BehaviorUploadFailed, "Warning"),
    ];
}

