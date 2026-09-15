namespace Athlon.Agent.Core.Plan;

/// <summary>
/// An approved plan artifact loaded back from disk.
/// </summary>
public sealed class PlanArtifact
{
    public required string SessionId { get; init; }

    /// <summary>The full plan markdown.</summary>
    public string? Markdown { get; set; }

    /// <summary>The <see cref="PlanRun"/> snapshot that produced the markdown, when available.</summary>
    public PlanRun? Run { get; set; }

    /// <summary>
    /// Absolute path of the markdown file on disk, when the artifact is file-backed.
    /// Null for in-memory-only artifacts or when the path cannot be resolved.
    /// </summary>
    public string? MarkdownPath { get; set; }

    public bool HasContent =>
        !string.IsNullOrWhiteSpace(Markdown)
        || (Run is not null && !string.IsNullOrWhiteSpace(Run.Title));
}

/// <summary>
/// Session-scoped durable storage for an approved plan. Distinct from <see cref="IPlanRunStore"/>,
/// which deliberately keeps the in-flight run in memory only: once the user clicks Build the plan
/// no longer needs a live run, but the model still needs the plan text many turns later — long
/// after the approved-plan user message has fallen out of the compaction window.
/// </summary>
public interface IPlanArtifactStore
{
    /// <summary>Persists the approved plan markdown and its run snapshot for the session.</summary>
    Task SaveAsync(
        string sessionId,
        string markdown,
        PlanRun? run,
        CancellationToken cancellationToken = default);

    /// <summary>Loads the session's approved plan, or null when none was ever persisted.</summary>
    Task<PlanArtifact?> LoadAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Deletes the session's persisted plan artifacts. Safe to call when none exist.</summary>
    Task ClearAsync(string sessionId, CancellationToken cancellationToken = default);
}
