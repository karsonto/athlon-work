using System.Text.Json;
using Athlon.Agent.Core;
using Athlon.Agent.Core.RuntimeDiagnostics;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Infrastructure.RuntimeDiagnostics;

namespace Athlon.Agent.Tests;

public sealed class DiagnoseLogsToolTests
{
    [Fact]
    public async Task Diagnose_logs_picks_highest_severity_error_code_as_root_cause()
    {
        using var temp = new TempDirectoryScope("diagnose-logs-tool");
        var paths = new TestAppPathProvider(temp.Root);

        var runContextAccessor = new NullAgentRunContextAccessor();
        var sink = new RuntimeDiagnosticEventSink(
            new NoOpLogger(),
            paths,
            new JsonFileStore(),
            runContextAccessor);

        // Two events for same session: Critical should win over Error.
        await sink.EnqueueAsync(new RuntimeDiagnosticEvent(
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
            component: RuntimeDiagnosticComponent.UiWebview,
            phase: RuntimeDiagnosticPhase.Initialize,
            eventType: "test.event.1",
            severity: RuntimeDiagnosticSeverity.Error,
            errorCode: RuntimeDiagnosticErrorCodes.UiWebviewInitFailed,
            message: "error"));

        await sink.EnqueueAsync(new RuntimeDiagnosticEvent(
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
            component: RuntimeDiagnosticComponent.UiWebview,
            phase: RuntimeDiagnosticPhase.Invoke,
            eventType: "test.event.2",
            severity: RuntimeDiagnosticSeverity.Critical,
            errorCode: RuntimeDiagnosticErrorCodes.UiWebviewScriptFailed,
            message: "critical"));

        await sink.FlushAsync();
        sink.Dispose();

        var tool = new DiagnoseLogsTool(
            paths,
            runContextAccessor,
            new FixedActiveSessionContext("s1"));

        var invocation = new ToolInvocation(
            toolName: "diagnose_logs",
            arguments: new Dictionary<string, string>
            {
                ["session_id"] = "s1",
                ["tail"] = "10"
            },
            explanation: null);

        var result = await tool.InvokeAsync(invocation);
        Assert.True(result.Succeeded);

        using var doc = JsonDocument.Parse(result.Content!);
        var rootCause = doc.RootElement.GetProperty("rootCause").GetString();
        Assert.Equal(RuntimeDiagnosticErrorCodes.UiWebviewScriptFailed, rootCause);
        Assert.True(doc.RootElement.TryGetProperty("evidenceQuality", out var evidenceQuality));
        Assert.True(evidenceQuality.TryGetProperty("score", out var score));
        Assert.True(score.GetDouble() >= 0);
        Assert.True(doc.RootElement.TryGetProperty("missingSignals", out var missingSignals));
        Assert.Equal(JsonValueKind.Array, missingSignals.ValueKind);
    }

    private sealed class NoOpLogger : IAppLogger
    {
        public IAppLogger ForContext(string sourceContext) => this;
        public void Debug(string messageTemplate, params object[] values) { }
        public void Information(string messageTemplate, params object[] values) { }
        public void Warning(string messageTemplate, params object[] values) { }
        public void Error(Exception exception, string messageTemplate, params object[] values) { }
        public void Dispose() { }
    }

    private sealed class NullAgentRunContextAccessor : IAgentRunContextAccessor
    {
        public AgentRunContext? Current => null;
        public IDisposable Push(AgentRunContext context) => new Scope();
        public string ResolveSessionDirectory(string sessionsPath, string sessionId) =>
            Path.Combine(sessionsPath, sessionId);

        private sealed class Scope : IDisposable
        {
            public void Dispose() { }
        }
    }

    private sealed class FixedActiveSessionContext(string sessionId) : IActiveAgentSessionContext
    {
        public string? SessionId => sessionId;
        public void SetSession(string? sessionId) { }
        public IDisposable Enter(string sessionId) => new Scope();

        private sealed class Scope : IDisposable
        {
            public void Dispose() { }
        }
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

    // TempDirectoryScope is provided by tests/TestDoubles.cs
}

