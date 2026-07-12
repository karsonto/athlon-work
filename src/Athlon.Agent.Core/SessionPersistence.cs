namespace Athlon.Agent.Core;

public sealed record SessionToolCallLogEntry(
    DateTimeOffset Timestamp,
    string ToolCallId,
    string ToolName,
    ToolCallArguments Arguments,
    bool Succeeded,
    string? Summary,
    string? Content,
    string? Error,
    long DurationMs);
