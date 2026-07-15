namespace Athlon.Agent.Core.BehaviorReport;

public sealed class BehaviorEvent
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Stable event identifier (e.g. model_call, mcp_tool).</summary>
    public string EventId { get; init; } = "";

    /// <summary>action or event.</summary>
    public string EventType { get; init; } = "event";

    /// <summary>API message_content; typically same as EventId.</summary>
    public string MessageContent { get; init; } = "";

    public Dictionary<string, object?> Parameters { get; init; } = new(StringComparer.Ordinal);
}

public static class BehaviorEventIds
{
    public const string AppStart = "app_start";
    public const string AppShutdown = "app_shutdown";
    public const string UserLogin = "user_login";
    public const string UserLoginFailed = "user_login_failed";
    public const string UserSession = "user_session";
    public const string AppUpdateCheck = "app_update_check";
    public const string ModelCall = "model_call";
    public const string ModelUsageSummary = "model_usage_summary";
    public const string McpTool = "mcp_tool";
    public const string McpServer = "mcp_server";
    public const string SkillLoad = "skill_load";
    public const string SkillToggle = "skill_toggle";
    public const string ToolInvoke = "tool_invoke";
    public const string ToolApproval = "tool_approval";
    public const string UserMessageSent = "user_message_sent";
    public const string Turn = "turn";
    public const string Context = "context";
    public const string Subagent = "subagent";
}

public static class BehaviorEventTypes
{
    public const string Action = "action";
    public const string Event = "event";
}
