namespace Athlon.Agent.Core.Teams;

public sealed class TeamDefinition
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string Description { get; set; } = "";

    public string LeaderProfileId { get; set; } = "";

    public List<string> WorkerProfileIds { get; set; } = new();

    public string? DefaultWorkspacePath { get; set; }

    public int MaxParallelWorkers { get; set; } = 3;

    public int? MaxTaskDurationSeconds { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
