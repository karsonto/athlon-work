using System.Collections.Concurrent;
using System.Diagnostics;
using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure;

/// <summary>
/// On-disk layout: only <c>sessions/{id}/session.json</c> (direct children) are main chats.
/// Sub-agent transcripts live under <c>sessions/{parent}/subagents/default/{id}/</c>.
///
/// <para>Resolving whether an id is a nested sub-agent session previously required enumerating
/// every session directory and stat-ing <c>subagents/default/{id}</c> under each one. That probe ran
/// on <em>every</em> path resolution (session load, display page read, and even directory creation
/// on save), so a single session switch paid for it dozens of times. The nested index below is
/// built at most once per TTL and reused; <see cref="ProbeCount"/> / <see cref="ProbeElapsed"/>
/// expose the remaining cost so it can be attributed in the switch profiler.</para>
/// </summary>
internal static class SessionDirectoryLayout
{
    public const string SubAgentsFolder = "subagents";
    public const string SubAgentKind = "default";

    /// <summary>
    /// Safety net for a missed invalidation. Nested sub-agent sessions are created rarely, so a
    /// short TTL keeps a newly spawned sub-agent discoverable even if its creator forgot to call
    /// <see cref="InvalidateNestedIndex(string?)"/>.
    /// </summary>
    internal static readonly TimeSpan NestedIndexTtl = TimeSpan.FromSeconds(5);

    private static readonly ConcurrentDictionary<string, NestedIndex> NestedIndexes =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly object NestedIndexGate = new();

    private static long _probeCount;
    private static long _probeElapsedTicks;

    /// <summary>Number of full directory enumerations performed since the last reset.</summary>
    public static long ProbeCount => Interlocked.Read(ref _probeCount);

    /// <summary>Total wall time spent in those enumerations since the last reset.</summary>
    public static TimeSpan ProbeElapsed => TimeSpan.FromTicks(Interlocked.Read(ref _probeElapsedTicks));

    /// <summary>Restarts the probe counters. Called when a session-switch measurement begins.</summary>
    public static void ResetProbeStats()
    {
        Interlocked.Exchange(ref _probeCount, 0);
        Interlocked.Exchange(ref _probeElapsedTicks, 0);
    }

    public static bool IsTopLevelSessionDirectory(string sessionsPath, string sessionDirectory)
    {
        var normalizedRoot = Path.GetFullPath(sessionsPath);
        var normalizedDir = Path.GetFullPath(sessionDirectory);
        var parent = Path.GetDirectoryName(normalizedDir);
        return parent is not null
            && string.Equals(parent, normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves the directory that actually holds a session's artifacts.
    ///
    /// <para>A sub-agent transcript is addressed by its own id, but lives under its parent's
    /// directory; the run context only knows the parent when the caller is the sub-agent itself.
    /// That is why the nested lookup exists at all. Routing every caller through this one method
    /// keeps the rule in a single place instead of four slightly different copies.</para>
    /// </summary>
    public static string ResolveEffectiveSessionDirectory(
        string sessionsPath,
        string sessionId,
        string resolvedDirectory,
        AgentRunKind runKind)
    {
        if (runKind == AgentRunKind.SubAgent)
        {
            return resolvedDirectory;
        }

        if (!IsTopLevelSessionDirectory(sessionsPath, resolvedDirectory))
        {
            return resolvedDirectory;
        }

        return TryFindNestedSubAgentDirectory(sessionsPath, sessionId) ?? resolvedDirectory;
    }

    /// <summary>Sub-agent session ids to their nested directory, for one sessions root.</summary>
    public static IReadOnlyDictionary<string, string> GetNestedIndex(string sessionsPath)
    {
        var normalizedRoot = Path.GetFullPath(sessionsPath);
        lock (NestedIndexGate)
        {
            if (NestedIndexes.TryGetValue(normalizedRoot, out var cached)
                && !cached.IsExpired(NestedIndexTtl))
            {
                return cached.Entries;
            }
        }

        var built = BuildNestedIndex(normalizedRoot);
        lock (NestedIndexGate)
        {
            NestedIndexes[normalizedRoot] = built;
        }

        return built.Entries;
    }

    /// <summary>
    /// Drops the cached index for a sessions root (or every root when <paramref name="sessionsPath"/>
    /// is null). Must be called when a sub-agent directory is created or removed, otherwise a new
    /// sub-agent is only discoverable once the TTL expires.
    /// </summary>
    public static void InvalidateNestedIndex(string? sessionsPath = null)
    {
        lock (NestedIndexGate)
        {
            if (string.IsNullOrWhiteSpace(sessionsPath))
            {
                NestedIndexes.Clear();
                return;
            }

            NestedIndexes.TryRemove(Path.GetFullPath(sessionsPath), out _);
        }
    }

    public static HashSet<string> CollectNestedSubAgentSessionIds(string sessionsPath)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in GetNestedIndex(sessionsPath).Keys)
        {
            ids.Add(id);
        }

        return ids;
    }

    public static string? TryFindNestedSubAgentDirectory(string sessionsPath, string subSessionId)
    {
        if (string.IsNullOrWhiteSpace(subSessionId))
        {
            return null;
        }

        return GetNestedIndex(sessionsPath).TryGetValue(subSessionId, out var directory)
            ? directory
            : null;
    }

    public static bool IsNestedSubAgentSessionId(string sessionsPath, string sessionId) =>
        TryFindNestedSubAgentDirectory(sessionsPath, sessionId) is not null;

    public static IEnumerable<string> EnumerateTopLevelSessionJsonPaths(string sessionsPath)
    {
        if (!Directory.Exists(sessionsPath))
        {
            yield break;
        }

        foreach (var sessionDir in Directory.EnumerateDirectories(sessionsPath))
        {
            var sessionJson = Path.Combine(sessionDir, "session.json");
            if (File.Exists(sessionJson))
            {
                yield return sessionJson;
            }
        }
    }

    public static bool IsEligibleForSessionMenu(
        string sessionsPath,
        SessionIndexEntry entry,
        ISet<string>? nestedSubAgentSessionIds = null) =>
        IsTopLevelSessionDirectory(sessionsPath, entry.Path)
        && !(nestedSubAgentSessionIds ?? CollectNestedSubAgentSessionIds(sessionsPath)).Contains(entry.Id);

    /// <summary>
    /// Enumerates every session directory once and records where each nested sub-agent session
    /// lives. One pass replaces the per-resolution scan: a directory without a
    /// <c>subagents/default</c> child costs a single failed existence check.
    /// </summary>
    private static NestedIndex BuildNestedIndex(string normalizedRoot)
    {
        var started = Stopwatch.GetTimestamp();
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (Directory.Exists(normalizedRoot))
            {
                foreach (var parentDir in Directory.EnumerateDirectories(normalizedRoot))
                {
                    var subAgentsRoot = Path.Combine(parentDir, SubAgentsFolder, SubAgentKind);
                    if (!Directory.Exists(subAgentsRoot))
                    {
                        continue;
                    }

                    foreach (var nestedDir in Directory.EnumerateDirectories(subAgentsRoot))
                    {
                        var subSessionId = Path.GetFileName(nestedDir);
                        if (!string.IsNullOrWhiteSpace(subSessionId))
                        {
                            entries[subSessionId] = nestedDir;
                        }
                    }
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
            // The root disappeared mid-enumeration; an empty index is correct and self-heals on TTL.
        }
        catch (UnauthorizedAccessException)
        {
            // Unreadable roots degrade to "not a sub-agent", matching the old probe's behavior.
        }

        Interlocked.Increment(ref _probeCount);
        Interlocked.Add(ref _probeElapsedTicks, Stopwatch.GetElapsedTime(started).Ticks);

        return new NestedIndex(entries, DateTimeOffset.UtcNow);
    }

    private sealed record NestedIndex(IReadOnlyDictionary<string, string> Entries, DateTimeOffset BuiltAt)
    {
        public bool IsExpired(TimeSpan ttl) => DateTimeOffset.UtcNow - BuiltAt > ttl;
    }
}
