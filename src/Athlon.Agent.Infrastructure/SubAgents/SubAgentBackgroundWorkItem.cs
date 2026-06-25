namespace Athlon.Agent.Infrastructure.SubAgents;

public sealed record SubAgentBackgroundWorkItem(
    string ParentSessionId,
    string SubSessionId,
    string SessionKey,
    string Role,
    string Message,
    string TaskId,
    string RunId);
