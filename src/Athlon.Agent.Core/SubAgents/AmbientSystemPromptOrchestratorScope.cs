using Athlon.Agent.Core.Prompt;

namespace Athlon.Agent.Core.SubAgents;

public sealed class AmbientSystemPromptOrchestratorScope : IDisposable
{
    private static readonly AsyncLocal<ISystemPromptOrchestrator?> Current = new();

    private readonly ISystemPromptOrchestrator? _previous;

    private AmbientSystemPromptOrchestratorScope(ISystemPromptOrchestrator orchestrator)
    {
        _previous = Current.Value;
        Current.Value = orchestrator;
    }

    public static ISystemPromptOrchestrator? CurrentOrchestrator => Current.Value;

    public static IDisposable Enter(ISystemPromptOrchestrator orchestrator) =>
        new AmbientSystemPromptOrchestratorScope(orchestrator);

    public void Dispose() => Current.Value = _previous;
}
