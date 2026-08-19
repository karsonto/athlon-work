namespace Athlon.Agent.Core.RuntimeDiagnostics;

public static class RuntimeDiagnosticErrorCodes
{
    // Model
    public const string ModelRequestFailed = "model.request_failed";
    public const string ModelResponseJsonInvalid = "model.response_json_invalid";
    public const string ModelToolCallJsonInvalid = "model.tool_call_json_invalid";
    public const string ModelContextLengthExceeded = "model.context_length_exceeded";
    public const string ModelStreamingIdleTimeout = "model.streaming_idle_timeout";
    public const string ModelStreamingFirstTokenTimeout = "model.streaming_first_token_timeout";
    public const string ModelStreamingInterrupted = "model.streaming_interrupted";

    // Tool
    public const string ToolInvokeFailed = "tool.invoke_failed";
    public const string ToolOutputEvicted = "tool.output_evicted";

    // Storage
    public const string StoragePersistFailed = "storage.persist_failed";
    public const string StorageLoadFailed = "storage.load_failed";

    // Compaction
    public const string CompactionSummaryFailed = "compaction.summary_failed";
    public const string CompactionRetrySkipped = "compaction.retry_skipped";
    public const string CompactionMiddleCutApplied = "compaction.middle_cut_applied";

    // UI
    public const string UiWebviewInitFailed = "ui.webview_init_failed";
    public const string UiWebviewScriptFailed = "ui.webview_script_failed";
    public const string UiSessionSwitchSurfaceMismatch = "ui.session_switch_surface_mismatch";

    // MCP
    public const string McpConnectFailed = "mcp.connect_failed";
    public const string McpListToolsFailed = "mcp.list_tools_failed";
    public const string McpToolInvokeFailed = "mcp.tool_invoke_failed";

    // SSH
    public const string SshConnectFailed = "ssh.connect_failed";

    // Subagent
    public const string SubagentRunFailed = "subagent.run_failed";

    // Behavior report
    public const string BehaviorUploadFailed = "behavior.upload_failed";
}

