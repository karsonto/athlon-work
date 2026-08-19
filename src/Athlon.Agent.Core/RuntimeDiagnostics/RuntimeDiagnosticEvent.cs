namespace Athlon.Agent.Core.RuntimeDiagnostics;

public sealed record RuntimeDiagnosticEvent(
    string eventId,
    DateTimeOffset ts,
    long sequence,
    string? sessionId,
    string? runId,
    string? turnId,
    string? attemptId,
    string? parentAttemptId,
    string? toolCallId,
    string? messageId,
    RuntimeDiagnosticComponent component,
    RuntimeDiagnosticPhase phase,
    string eventType,
    RuntimeDiagnosticSeverity severity,
    string? workspaceKind = null,
    string? workspaceId = null,
    string? workspaceRoot = null,
    string? model = null,
    string? provider = null,
    string? subagentRole = null,
    string? errorCode = null,
    string? message = null);

