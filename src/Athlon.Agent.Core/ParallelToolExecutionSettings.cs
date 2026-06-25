namespace Athlon.Agent.Core;

public sealed class ParallelToolExecutionSettings
{
    public bool Enabled { get; set; } = true;

    public int MaxDegreeOfParallelism { get; set; } = 4;
}
