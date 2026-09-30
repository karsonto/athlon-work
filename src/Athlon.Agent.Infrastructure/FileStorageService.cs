using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.RuntimeDiagnostics;
using Athlon.Agent.Infrastructure.BehaviorReport;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Athlon.Agent.Infrastructure;

public sealed partial class FileStorageService(
    IAppLogger logger,
    IAppPathProvider paths,
    IJsonFileStore jsonFileStore,
    IAgentRunContextAccessor runContextAccessor,
    IRuntimeDiagnosticEventSink? runtimeDiagnosticEventSink = null) : IFileStorageService
{
    private static readonly UTF8Encoding Utf8Bom = new(encoderShouldEmitUTF8Identifier: true);
    private readonly IAppLogger _logger = logger.ForContext("Storage");
    private readonly SessionIndexCoordinator _indexCoordinator = new(paths, jsonFileStore, runContextAccessor);
    private readonly IRuntimeDiagnosticEventSink? _runtimeDiagnosticEventSink = runtimeDiagnosticEventSink;
    private ToolCallLogWriteQueue? _toolCallLogQueue;
    private readonly ConcurrentDictionary<string, byte> _ensuredSessionDirs = new(StringComparer.OrdinalIgnoreCase);

    private ToolCallLogWriteQueue ToolCallLogQueue =>
        _toolCallLogQueue ??= new ToolCallLogWriteQueue(WriteToolCallLogCoreAsync, _logger);

    public string RootPath => paths.RootPath;


    private void EnsureSessionLogDirectories(string sessionId)
    {
        var sessionDir = GetSessionDirectory(sessionId);
        // Directory.CreateDirectory is cheap per call but this runs on every save (session,
        // conversation, display, tasks). The dictionary keeps the whole check off the disk for
        // sessions already prepared.
        if (_ensuredSessionDirs.ContainsKey(sessionDir))
        {
            return;
        }

        Directory.CreateDirectory(sessionDir);
        Directory.CreateDirectory(Path.Combine(sessionDir, "summaries"));
        Directory.CreateDirectory(Path.Combine(sessionDir, "transcripts"));
        Directory.CreateDirectory(Path.Combine(sessionDir, "evicted"));
        _ensuredSessionDirs.TryAdd(sessionDir, 0);
    }

    /// <summary>
    /// Creates the session log directories on the thread pool. Used from the post-first-paint adopt
    /// path, where the four <c>CreateDirectory</c> calls would otherwise run on the UI thread and
    /// stall the frame the user is looking at.
    /// </summary>
    private Task EnsureSessionLogDirectoriesAsync(string sessionId) =>
        BackgroundFileIo.RunAsync(() => EnsureSessionLogDirectories(sessionId));

    private string GetSessionDirectory(AgentSession session) => GetSessionDirectory(session.Id);

    private string GetSessionDirectory(string sessionId)
    {
        var resolved = runContextAccessor.ResolveSessionDirectory(paths.SessionsPath, sessionId);
        return SessionDirectoryLayout.ResolveEffectiveSessionDirectory(
            paths.SessionsPath,
            sessionId,
            resolved,
            runContextAccessor.Current?.Kind ?? AgentRunKind.Root);
    }

    private async Task EnqueueStorageDiagnosticAsync(
        string? sessionId,
        RuntimeDiagnosticPhase phase,
        string eventType,
        RuntimeDiagnosticSeverity severity,
        string errorCode,
        string? message)
    {
        if (_runtimeDiagnosticEventSink is not { } sink)
        {
            return;
        }

        var context = runContextAccessor.Current;
        var evt = new RuntimeDiagnosticEvent(
            eventId: "",
            ts: default,
            sequence: 0,
            sessionId: sessionId,
            runId: context?.RunId ?? sessionId,
            turnId: null,
            attemptId: null,
            parentAttemptId: null,
            toolCallId: null,
            messageId: null,
            component: RuntimeDiagnosticComponent.Storage,
            phase: phase,
            eventType: eventType,
            severity: severity,
            errorCode: errorCode,
            message: message);
        await sink.EnqueueAsync(evt, CancellationToken.None).ConfigureAwait(false);
    }


}
