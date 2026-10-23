using System.Collections.Concurrent;
using System.Text;
using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure.Storage;

public sealed class SessionDirectoryService(IAppPathProvider paths, IAgentRunContextAccessor runContextAccessor)
{
    public static readonly UTF8Encoding Utf8Bom = new(encoderShouldEmitUTF8Identifier: true);

    private readonly ConcurrentDictionary<string, byte> _ensuredSessionDirs = new(StringComparer.OrdinalIgnoreCase);

    public void EnsureSessionLogDirectories(string sessionId)
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
    public Task EnsureSessionLogDirectoriesAsync(string sessionId) =>
        BackgroundFileIo.RunAsync(() => EnsureSessionLogDirectories(sessionId));

    public string GetSessionDirectory(AgentSession session) => GetSessionDirectory(session.Id);

    public string GetSessionDirectory(string sessionId)
    {
        var resolved = runContextAccessor.ResolveSessionDirectory(paths.SessionsPath, sessionId);
        return SessionDirectoryLayout.ResolveEffectiveSessionDirectory(
            paths.SessionsPath,
            sessionId,
            resolved,
            runContextAccessor.Current?.Kind ?? AgentRunKind.Root);
    }

    public void TryForgetEnsuredDir(string sessionId)
    {
        // Ensure caches the effective session directory (may be nested for sub-agents). Forget that
        // key; also drop the top-level layout key in case Ensure ran under a different run context.
        _ensuredSessionDirs.TryRemove(GetSessionDirectory(sessionId), out _);
        _ensuredSessionDirs.TryRemove(Path.Combine(paths.SessionsPath, sessionId), out _);
    }
}
