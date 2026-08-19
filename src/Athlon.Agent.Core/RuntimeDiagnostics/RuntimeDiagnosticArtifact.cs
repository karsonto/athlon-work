namespace Athlon.Agent.Core.RuntimeDiagnostics;

public sealed record RuntimeDiagnosticArtifact(
    string name,
    string pointer,
    string? description = null);

