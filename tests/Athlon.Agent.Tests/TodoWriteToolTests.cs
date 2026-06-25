using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

public sealed class TodoWriteToolTests
{
    [Fact]
    public async Task Invoke_ReplacesTaskList_AndEnforcesSingleInProgress()
    {
        var store = new InMemorySessionTaskStore();
        var sessionContext = new TestActiveSessionContext("session-1");
        var tool = new Athlon.Agent.Infrastructure.TodoWriteTool(store, sessionContext);
        var tasksJson =
            """
            [
              {"id":"1","content":"read files","status":"completed"},
              {"id":"2","content":"edit file","status":"in_progress"}
            ]
            """;

        var result = await tool.InvokeAsync(
            new ToolInvocation("todo_write", new Dictionary<string, string> { ["tasks"] = tasksJson }));

        Assert.True(result.Succeeded);
        var saved = await store.LoadAsync("session-1");
        Assert.Equal(2, saved.Count);
        Assert.Equal(AgentTaskStatus.InProgress, saved[1].Status);
    }

    private sealed class InMemorySessionTaskStore : Athlon.Agent.Infrastructure.ISessionTaskStore
    {
        private readonly Dictionary<string, IReadOnlyList<AgentTaskItem>> _tasks = new(StringComparer.Ordinal);

        public Task<IReadOnlyList<AgentTaskItem>> LoadAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_tasks.GetValueOrDefault(sessionId) ?? Array.Empty<AgentTaskItem>());

        public Task SaveAsync(string sessionId, IReadOnlyList<AgentTaskItem> tasks, CancellationToken cancellationToken = default)
        {
            _tasks[sessionId] = tasks;
            return Task.CompletedTask;
        }
    }

    private sealed class TestActiveSessionContext(string sessionId) : IActiveAgentSessionContext
    {
        public string? SessionId => sessionId;
        public void SetSession(string? sessionId) { }
        public IDisposable Enter(string sessionId) => this;
        public void Dispose() { }
    }
}
