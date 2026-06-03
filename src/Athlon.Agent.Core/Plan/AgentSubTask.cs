namespace Athlon.Agent.Core.Plan;

public sealed class AgentSubTask
{
    public AgentSubTask(
        string name,
        string description,
        string expectedOutcome,
        IReadOnlyList<string>? files = null)
    {
        Name = name;
        Description = description;
        ExpectedOutcome = expectedOutcome;
        Files = NormalizeFiles(files);
        CreatedAt = DateTimeOffset.Now;
    }

    public string Name { get; }
    public string Description { get; }
    public string ExpectedOutcome { get; }
    public IReadOnlyList<string> Files { get; }
    public SubTaskState State { get; set; } = SubTaskState.Todo;
    public string? Outcome { get; private set; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset? FinishedAt { get; private set; }

    public void Finish(string outcome)
    {
        State = SubTaskState.Done;
        Outcome = outcome;
        FinishedAt = DateTimeOffset.Now;
    }

    private static IReadOnlyList<string> NormalizeFiles(IReadOnlyList<string>? files)
    {
        if (files is null || files.Count == 0)
        {
            return Array.Empty<string>();
        }

        return files
            .Where(file => !string.IsNullOrWhiteSpace(file))
            .Select(file => file.Trim().Replace('\\', '/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
