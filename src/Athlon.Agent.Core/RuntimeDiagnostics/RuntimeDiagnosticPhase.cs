namespace Athlon.Agent.Core.RuntimeDiagnostics;
public enum RuntimeDiagnosticPhase
{
    Initialize,
    Prepare,
    Request,
    Streaming,
    Parse,
    Invoke,
    Persist,
    Replay,
    Switch,
    Compact,
    Upload,
    Shutdown
}

