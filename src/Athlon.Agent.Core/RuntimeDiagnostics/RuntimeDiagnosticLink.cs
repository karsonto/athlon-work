namespace Athlon.Agent.Core.RuntimeDiagnostics;

public sealed record RuntimeDiagnosticLink(
    string name,
    string pointer,
    string? description = null);

