using System.IO;
using System.Text.Json;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Prompt;
using Athlon.Agent.Core.RuntimeDiagnostics;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Infrastructure.RuntimeDiagnostics;

namespace Athlon.Agent.Tests;

public sealed class RuntimeDiagnosticsEventSinkTests
{
    [Fact]
    public async Task EnqueueAsync_writes_runtime_events_jsonl_for_session()
    {
        using var temp = new TempDirectoryScope("runtime-diag-sink");
        var paths = new TestAppPathProvider(temp.Root);
        RuntimeDiagnosticEventSink sink;
        try
        {
            sink = new RuntimeDiagnosticEventSink(
                new NoOpLogger(),
                paths,
                new JsonFileStore(),
                new AgentRunContextAccessor());
        }
        catch (TypeLoadException ex)
        {
            // 把 inner exception 一并带出来，方便定位是缺依赖还是签名加载失败
            throw new Exception($"TypeLoadException while creating sink: {ex}\nInner: {ex.InnerException}", ex);
        }

        var evt = new RuntimeDiagnosticEvent(
            eventId: "",
            ts: default,
            sequence: 0,
            sessionId: "s1",
            runId: null,
            turnId: null,
            attemptId: null,
            parentAttemptId: null,
            toolCallId: null,
            messageId: null,
            component: RuntimeDiagnosticComponent.Model,
            phase: RuntimeDiagnosticPhase.Request,
            eventType: "test.event",
            severity: RuntimeDiagnosticSeverity.Error,
            errorCode: RuntimeDiagnosticErrorCodes.ModelRequestFailed,
            message: "failure");

        await sink.EnqueueAsync(evt);
        await sink.FlushAsync();

        var file = Path.Combine(paths.SessionsPath, "s1", "diagnostics", "runtime-events.jsonl");
        Assert.True(File.Exists(file));

        var line = await File.ReadAllLinesAsync(file);
        Assert.NotEmpty(line);

        using var doc = JsonDocument.Parse(line[0]);
        Assert.True(doc.RootElement.TryGetProperty("eventId", out var eventId));
        Assert.Equal("Model", doc.RootElement.GetProperty("component").GetString());
        Assert.True(doc.RootElement.TryGetProperty("sequence", out var seq));
        Assert.True(seq.GetInt64() > 0);

        var indexFile = Path.Combine(paths.SessionsPath, "s1", "diagnostics", "diagnostic-index.json");
        Assert.True(File.Exists(indexFile));
        using var indexDoc = JsonDocument.Parse(await File.ReadAllTextAsync(indexFile));
        Assert.True(indexDoc.RootElement.TryGetProperty("errorCodeCounts", out var errorCodeCounts));
        Assert.Equal(1, errorCodeCounts.GetProperty(RuntimeDiagnosticErrorCodes.ModelRequestFailed).GetInt32());
        Assert.True(indexDoc.RootElement.TryGetProperty("lastFailed", out var lastFailed));
        Assert.Equal(RuntimeDiagnosticErrorCodes.ModelRequestFailed, lastFailed.GetProperty("errorCode").GetString());

        sink.Dispose();
    }

    [Fact]
    public async Task EnqueueAsync_infers_session_id_from_active_context_when_missing()
    {
        using var temp = new TempDirectoryScope("runtime-diag-sink-infer-active");
        var paths = new TestAppPathProvider(temp.Root);
        var activeSession = new NoOpActiveAgentSessionContext();
        activeSession.SetSession("s-active");
        var sink = new RuntimeDiagnosticEventSink(
            new NoOpLogger(),
            paths,
            new JsonFileStore(),
            new AgentRunContextAccessor(),
            activeSession);

        await sink.EnqueueAsync(new RuntimeDiagnosticEvent(
            eventId: "",
            ts: default,
            sequence: 0,
            sessionId: null,
            runId: null,
            turnId: null,
            attemptId: null,
            parentAttemptId: null,
            toolCallId: null,
            messageId: null,
            component: RuntimeDiagnosticComponent.Model,
            phase: RuntimeDiagnosticPhase.Request,
            eventType: "test.active.infer",
            severity: RuntimeDiagnosticSeverity.Error,
            errorCode: RuntimeDiagnosticErrorCodes.ModelRequestFailed,
            message: "failure"));
        await sink.FlushAsync();

        var file = Path.Combine(paths.SessionsPath, "s-active", "diagnostics", "runtime-events.jsonl");
        Assert.True(File.Exists(file));
        sink.Dispose();
    }

    [Fact]
    public async Task EnqueueAsync_prefers_run_context_session_id_when_missing()
    {
        using var temp = new TempDirectoryScope("runtime-diag-sink-infer-run");
        var paths = new TestAppPathProvider(temp.Root);
        var runContextAccessor = new AgentRunContextAccessor();
        var activeSession = new NoOpActiveAgentSessionContext();
        activeSession.SetSession("s-active");
        var sink = new RuntimeDiagnosticEventSink(
            new NoOpLogger(),
            paths,
            new JsonFileStore(),
            runContextAccessor,
            activeSession);

        var runContext = AgentRunContext.CreateRoot(
            new AgentSession("s-run", "title", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, null, []),
            "r1",
            new NoOpToolRouter(),
            new NoOpPromptOrchestrator(),
            WorkspaceIgnoreDefaults.BuiltIn);
        using var scope = runContextAccessor.Push(runContext);

        await sink.EnqueueAsync(new RuntimeDiagnosticEvent(
            eventId: "",
            ts: default,
            sequence: 0,
            sessionId: null,
            runId: null,
            turnId: null,
            attemptId: null,
            parentAttemptId: null,
            toolCallId: null,
            messageId: null,
            component: RuntimeDiagnosticComponent.Model,
            phase: RuntimeDiagnosticPhase.Request,
            eventType: "test.run.infer",
            severity: RuntimeDiagnosticSeverity.Error,
            errorCode: RuntimeDiagnosticErrorCodes.ModelRequestFailed,
            message: "failure"));
        await sink.FlushAsync();

        var file = Path.Combine(paths.SessionsPath, "s-run", "diagnostics", "runtime-events.jsonl");
        Assert.True(File.Exists(file));
        sink.Dispose();
    }

    private sealed class TestAppPathProvider(string root) : IAppPathProvider
    {
        public string RootPath { get; } = root;
        public string ConfigPath => Path.Combine(RootPath, "config");
        public string SessionsPath => Path.Combine(RootPath, "sessions");
        public string AuditPath => Path.Combine(RootPath, "audit");
        public string LogsPath => Path.Combine(RootPath, "logs");
        public string CredentialsPath => Path.Combine(RootPath, "credentials");
        public string SkillsPath => Path.Combine(RootPath, "skills");

        public void EnsureCreated()
        {
            Directory.CreateDirectory(RootPath);
            Directory.CreateDirectory(ConfigPath);
            Directory.CreateDirectory(SessionsPath);
            Directory.CreateDirectory(AuditPath);
            Directory.CreateDirectory(LogsPath);
            Directory.CreateDirectory(CredentialsPath);
            Directory.CreateDirectory(SkillsPath);
        }

        public string ResolveSkillPath(string path) =>
            string.IsNullOrWhiteSpace(path) ? path : Path.IsPathRooted(path) ? path : Path.Combine(SkillsPath, path);
    }

    private sealed class NoOpPromptOrchestrator : ISystemPromptOrchestrator
    {
        public FrozenSystemPrompt PrepareForTurn(AgentSession session, IReadOnlyList<ToolDefinition> tools) =>
            new("");

        public string? BuildRuntimeContext(AgentSession session, IReadOnlyList<ToolDefinition> tools) => null;

        public string BuildForReasoningIteration(FrozenSystemPrompt frozen, AgentSession session, IReadOnlyList<ToolDefinition> tools) =>
            frozen.Text;
    }
}

