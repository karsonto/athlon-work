using System.Text.Json.Serialization;

namespace Athlon.Agent.Core;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AgentTaskStatus
{
    Pending,
    InProgress,
    Completed,
    Cancelled
}

public sealed record AgentTaskItem(string Id, string Content, AgentTaskStatus Status);
