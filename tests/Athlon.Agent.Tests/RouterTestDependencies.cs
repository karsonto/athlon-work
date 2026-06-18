using Athlon.Agent.Core;
using Athlon.Agent.Core.Knowledge;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

internal static class RouterTestDependencies
{
    public static ActiveAgentSessionContext CreateSessionContext()
    {
        var context = new ActiveAgentSessionContext();
        context.SetSession("test-session");
        return context;
    }

    public static ISessionKnowledgeState CreateSessionKnowledgeState(
        bool enabled = false,
        params string[] moduleIds) =>
        new StubSessionKnowledgeState(new SessionKnowledgeSnapshot(
            enabled,
            moduleIds.ToHashSet(StringComparer.OrdinalIgnoreCase)));

    internal sealed class StubSessionKnowledgeState(SessionKnowledgeSnapshot snapshot) : ISessionKnowledgeState
    {
        public Task LoadAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveAsync(string sessionId, SessionKnowledgeSnapshot state, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public SessionKnowledgeSnapshot GetSnapshot(string? sessionId) => snapshot;

        public bool ShouldExposeKnowledgeTool(string? sessionId) =>
            snapshot.Enabled && snapshot.ModuleIds.Count > 0;

        public Task<IReadOnlySet<string>> GetModuleIdsAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlySet<string>>(snapshot.Enabled ? snapshot.ModuleIds : new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }
}
