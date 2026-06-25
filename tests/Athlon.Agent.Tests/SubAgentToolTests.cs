using Athlon.Agent.Core;
using Athlon.Agent.Core.SubAgents;
using Athlon.Agent.Infrastructure.SubAgents;

namespace Athlon.Agent.Tests;

public sealed class SubAgentToolTests
{
    [Fact]
    public async Task InvokeAsync_NewSession_RequiresRole()
    {
        var context = new NoOpActiveAgentSessionContext();
        var tool = CreateTool(new StubSessionManager(), context: context);
        using var parent = context.Enter("parent-1");

        var result = await tool.InvokeAsync(new ToolInvocation(
            "call_assistant",
            new Dictionary<string, string> { ["message"] = "do work" }));

        Assert.False(result.Succeeded);
        Assert.Contains("role", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvokeAsync_NewSession_ReturnsSessionId()
    {
        var context = new NoOpActiveAgentSessionContext();
        var manager = new StubSessionManager();
        var tool = CreateTool(manager, context: context);
        using var parent = context.Enter("parent-1");

        var result = await tool.InvokeAsync(new ToolInvocation(
            "call_assistant",
            new Dictionary<string, string>
            {
                ["role"] = "Searcher",
                ["message"] = "find todos"
            }));

        Assert.True(result.Succeeded);
        Assert.Contains("session_id:", result.Content ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, manager.SpawnCount);
    }

    [Fact]
    public async Task InvokeAsync_Continue_UsesSend()
    {
        var context = new NoOpActiveAgentSessionContext();
        var manager = new StubSessionManager();
        var tool = CreateTool(manager, context: context);
        using var parent = context.Enter("parent-1");

        var result = await tool.InvokeAsync(new ToolInvocation(
            "call_assistant",
            new Dictionary<string, string>
            {
                ["session_id"] = "sub-continue",
                ["message"] = "continue"
            }));

        Assert.True(result.Succeeded);
        Assert.Equal(1, manager.SendCount);
        Assert.Equal("sub-continue", manager.LastSendSessionKey?.Split(':').Last());
    }

    [Fact]
    public async Task InvokeAsync_ExceedsNestingDepth_Fails()
    {
        var context = new NoOpActiveAgentSessionContext();
        var settings = new AppSettings { SubAgent = new SubAgentSettings { MaxNestingDepth = 1 } };
        var tool = CreateTool(new StubSessionManager(), settings, context);
        using var parent = context.Enter("parent-1");
        using var depth = SubAgentExecutionScope.Enter();

        var result = await tool.InvokeAsync(new ToolInvocation(
            "call_assistant",
            new Dictionary<string, string>
            {
                ["role"] = "x",
                ["message"] = "y"
            }));

        Assert.False(result.Succeeded);
        Assert.Contains("depth", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static SubAgentTool CreateTool(
        ISubAgentSessionManager sessionManager,
        AppSettings? settings = null,
        IActiveAgentSessionContext? context = null)
    {
        settings ??= new AppSettings();
        context ??= new NoOpActiveAgentSessionContext();
        return new SubAgentTool(
            settings,
            new Lazy<ISubAgentSessionManager>(() => sessionManager),
            context);
    }

    private sealed class StubSessionManager : ISubAgentSessionManager
    {
        public int SpawnCount { get; private set; }
        public int SendCount { get; private set; }
        public string? LastSendSessionKey { get; private set; }

        public Task<SpawnResult> SpawnAsync(
            string parentSessionId,
            string role,
            string? message,
            string? label,
            int? timeoutSeconds,
            CancellationToken cancellationToken = default)
        {
            SpawnCount++;
            var subId = Guid.NewGuid().ToString("N");
            var key = SubAgentSessionKey.Build(parentSessionId, subId);
            var reply = SubAgentResultFormatter.FormatTrustedReply(key, subId, "/tmp/session.json", "done from sub");
            return Task.FromResult(new SpawnResult("ok", "run_x", key, subId, "/tmp/session.json", null, null, false, reply));
        }

        public Task<SendResult> SendAsync(
            string parentSessionId,
            string? sessionKey,
            string? label,
            string message,
            int? timeoutSeconds,
            CancellationToken cancellationToken = default)
        {
            SendCount++;
            LastSendSessionKey = sessionKey;
            var key = sessionKey ?? string.Empty;
            var subId = key.Split(':').LastOrDefault() ?? "sub";
            var reply = SubAgentResultFormatter.FormatTrustedReply(key, subId, "/tmp/session.json", "done from sub");
            return Task.FromResult(new SendResult("ok", key, reply, null, null));
        }

        public Task<IReadOnlyList<SubAgentSessionEntry>> ListAsync(string parentSessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SubAgentSessionEntry>>(Array.Empty<SubAgentSessionEntry>());

        public Task<HistoryResult> HistoryAsync(string parentSessionId, string sessionKey, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HistoryResult(sessionKey, null, null, null));

        public Task<IReadOnlyList<PendingCompletion>> DrainCompletionsAsync(string parentSessionId, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PendingCompletion>>(Array.Empty<PendingCompletion>());

        public Task<SubAgentTaskRecord?> GetTaskOutputAsync(string parentSessionId, string taskId, CancellationToken cancellationToken = default) =>
            Task.FromResult<SubAgentTaskRecord?>(null);
    }

    private sealed class NoOpActiveAgentSessionContext : IActiveAgentSessionContext
    {
        private string? _sessionId;

        public string? SessionId => _sessionId;

        public void SetSession(string? sessionId) => _sessionId = sessionId;

        public IDisposable Enter(string sessionId)
        {
            var previous = _sessionId;
            _sessionId = sessionId;
            return new Scope(this, previous);
        }

        private sealed class Scope(NoOpActiveAgentSessionContext owner, string? previous) : IDisposable
        {
            public void Dispose() => owner._sessionId = previous;
        }
    }
}
