using System.Collections.Concurrent;
using Athlon.Agent.Core.Plan;

namespace Athlon.Agent.Infrastructure.Plan;

/// <summary>
/// Memory-only plan store. Plans are deliberately never persisted: the run lives just long
/// enough for the user to review it and click Build, at which point the plan text is injected
/// into the conversation as a normal message and the run is cleared. This keeps reloads and
/// session switches from resurrecting or re-injecting a plan the user already acted on.
/// </summary>
public sealed class InMemoryPlanRunStore : IPlanRunStore
{
    private readonly ConcurrentDictionary<string, PlanRun> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _markdown = new(StringComparer.OrdinalIgnoreCase);

    public Task<PlanRun?> LoadActiveAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return Task.FromResult<PlanRun?>(null);
        }

        return Task.FromResult(
            _active.TryGetValue(sessionId, out var run) ? run.Clone() : null);
    }

    public Task SaveActiveAsync(PlanRun run, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(run.SessionId))
        {
            return Task.CompletedTask;
        }

        _active[run.SessionId] = run.Clone();
        if (!string.IsNullOrWhiteSpace(run.PlanMarkdown))
        {
            _markdown[run.SessionId] = run.PlanMarkdown;
        }

        return Task.CompletedTask;
    }

    public Task ClearActiveAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return Task.CompletedTask;
        }

        _active.TryRemove(sessionId, out _);
        _markdown.TryRemove(sessionId, out _);
        return Task.CompletedTask;
    }

    public Task WritePlanMarkdownAsync(string sessionId, string markdown, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            _markdown[sessionId] = markdown ?? string.Empty;
        }

        return Task.CompletedTask;
    }

    public Task<string?> ReadPlanMarkdownAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return Task.FromResult<string?>(null);
        }

        return Task.FromResult(_markdown.TryGetValue(sessionId, out var markdown) ? markdown : null);
    }
}
