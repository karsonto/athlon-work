namespace Athlon.Agent.Core.Compaction;

public sealed record CompactionRuntimeContext(
    ContextBudgetSnapshot Budget,
    string EnvironmentPrompt,
    IReadOnlyList<ToolDefinition> Tools,
    double CalibrationMultiplier = 1.0,
    ContextPressureLevel PressureOverride = ContextPressureLevel.Normal,
    int? LastActualPromptTokens = null)
{
    public bool ForceOverflow => PressureOverride == ContextPressureLevel.Overflow;
}
