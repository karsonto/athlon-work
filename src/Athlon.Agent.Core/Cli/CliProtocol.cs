namespace Athlon.Agent.Core.Cli;

public static class CliSseEventNames
{
    public const string Text = "text";
    public const string ToolStart = "tool_start";
    public const string ToolEnd = "tool_end";
    public const string ToolOutput = "tool_output";
    public const string ApprovalRequired = "approval_required";
    public const string Error = "error";
    public const string Done = "done";
    public const string Session = "session";
}

public sealed record CliEndpointInfo
{
    public required string Url { get; init; }
    public required string Token { get; init; }
    public required int Pid { get; init; }
}

public sealed record CliTurnRequest
{
    public string Cwd { get; init; } = "";
    public string Input { get; init; } = "";
    public string? SessionId { get; init; }
    public bool NewSession { get; init; }
}

public sealed record CliApprovalRequest
{
    public string ToolCallId { get; init; } = "";
    public string Decision { get; init; } = "";
}

public sealed record CliErrorResponse(string Error);

public sealed record CliTextPayload(string Delta);

public sealed record CliToolStartPayload(string Id, string Name);

public sealed record CliToolEndPayload(string Id);

public sealed record CliToolOutputPayload(string Id, string Delta);

public sealed record CliApprovalRequiredPayload(string ToolCallId, string ToolName);

public sealed record CliDonePayload(string SessionId);

public sealed record CliErrorPayload(string Message);

public sealed record CliLaunchOptions(
    bool Once,
    bool Yes,
    string? SessionId,
    string? Prompt);

public enum CliReplCommandKind
{
    Message,
    Exit,
    New,
    Empty
}

public interface IDesktopSessionRunProbe
{
    bool IsSessionRunning(string sessionId);
}

public sealed class NullDesktopSessionRunProbe : IDesktopSessionRunProbe
{
    public static readonly NullDesktopSessionRunProbe Instance = new();

    public bool IsSessionRunning(string sessionId) => false;
}
