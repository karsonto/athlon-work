namespace Athlon.Agent.Core.SubAgents;

public sealed record SubAgentSessionEntry(
    string SessionKey,
    string SubSessionId,
    string ParentSessionId,
    string Role,
    string? Label,
    string SpawnRunId,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt,
    string SessionFilePath,
    int MessageCount);

public sealed record SpawnResult(
    string Status,
    string? RunId,
    string SessionKey,
    string SubSessionId,
    string SessionFilePath,
    string? Error,
    string? TaskId,
    bool ReusedExisting,
    string? Reply)
{
    public bool IsOk => string.Equals(Status, "ok", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Status, "accepted", StringComparison.OrdinalIgnoreCase);
}

public sealed record SendResult(
    string Status,
    string SessionKey,
    string? Reply,
    string? Error,
    string? TaskId)
{
    public bool IsOk => string.Equals(Status, "ok", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Status, "accepted", StringComparison.OrdinalIgnoreCase);
}

public sealed record PendingCompletion(
    string RunId,
    string ChildSessionKey,
    string RequesterSessionKey,
    string Status,
    string? ResultText,
    string? Error,
    DateTimeOffset CompletedAt,
    string AnnounceText);

public sealed record SubAgentTaskRecord(
    string TaskId,
    string ParentSessionId,
    string SessionKey,
    string SubSessionId,
    string Status,
    string? Result,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

public sealed record HistoryResult(
    string? SessionKey,
    string? SessionFilePath,
    string? Content,
    string? Error);

public sealed class SubAgentMetaFile
{
    public string Role { get; set; } = "";
    public string? Label { get; set; }
    public string SpawnRunId { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastActivityAt { get; set; }
}

public sealed class SubAgentTaskListFile
{
    public List<SubAgentTaskRecord> Tasks { get; set; } = [];
}
