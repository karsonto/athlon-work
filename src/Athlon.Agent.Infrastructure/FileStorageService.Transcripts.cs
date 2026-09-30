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

/// <summary>Transcripts, evicted tool results and handoff notes.</summary>
public sealed partial class FileStorageService
{
    public async Task<string> SaveTranscriptAsync(string sessionId, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        using (await SessionWriteLock.AcquireAsync(sessionId, cancellationToken).ConfigureAwait(false))
        {
            var transcriptDir = GetSessionTranscriptsDirectory(sessionId);
            Directory.CreateDirectory(transcriptDir);
            var path = Path.Combine(transcriptDir, $"transcript_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.jsonl");

            var builder = new StringBuilder();
            foreach (var message in messages)
            {
                builder.AppendLine(JsonSerializer.Serialize(message, JsonFileStore.JsonLineOptions));
            }

            await FileIoRetry.RunAsync(
                () => File.WriteAllTextAsync(path, builder.ToString(), Utf8Bom, cancellationToken),
                cancellationToken);
            return path;
        }
    }

    public async Task<string> SaveEvictedToolResultAsync(
        string sessionId,
        string toolCallId,
        string content,
        CancellationToken cancellationToken = default)
    {
        using (await SessionWriteLock.AcquireAsync(sessionId, cancellationToken).ConfigureAwait(false))
        {
            var evictedDir = Path.Combine(GetSessionDirectory(sessionId), "evicted");
            Directory.CreateDirectory(evictedDir);
            var path = Path.Combine(evictedDir, $"{toolCallId}.txt");
            await AtomicFile.WriteAllTextAsync(path, content, cancellationToken);
            return path;
        }
    }

    public async Task<string?> TryReadEvictedToolResultAsync(
        string sessionId,
        string toolCallId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(toolCallId))
        {
            return null;
        }

        using (await SessionWriteLock.AcquireAsync(sessionId, cancellationToken).ConfigureAwait(false))
        {
            var path = Path.Combine(GetSessionDirectory(sessionId), "evicted", $"{toolCallId}.txt");
            if (!File.Exists(path))
            {
                return null;
            }

            return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<SessionTranscriptInfo>> ListSessionTranscriptsAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var transcriptDir = GetSessionTranscriptsDirectory(sessionId);
        if (!Directory.Exists(transcriptDir))
        {
            return [];
        }

        var entries = new List<SessionTranscriptInfo>();
        foreach (var path in Directory.EnumerateFiles(transcriptDir, "transcript_*.jsonl"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(path);
            if (!IsTranscriptFileName(fileName))
            {
                continue;
            }

            var messageCount = 0;
            await foreach (var line in File.ReadLinesAsync(path, cancellationToken).ConfigureAwait(false))
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    messageCount++;
                }
            }

            entries.Add(new SessionTranscriptInfo(fileName, File.GetLastWriteTimeUtc(path), messageCount));
        }

        entries.Sort(static (left, right) => right.WrittenAt.CompareTo(left.WrittenAt));
        return entries;
    }

    public async Task<string?> ReadSessionTranscriptAsync(
        string sessionId,
        string fileName,
        int offsetChars,
        int limitChars,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolveTranscriptPath(sessionId, fileName, out var path) || !File.Exists(path))
        {
            return null;
        }

        var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        if (offsetChars < 0)
        {
            offsetChars = 0;
        }

        if (offsetChars >= text.Length)
        {
            return string.Empty;
        }

        var take = Math.Clamp(limitChars, 1, ContextTokenEstimator.EstimateCharacterBudget(SessionArchiveLimits.MaxToolOutputTokens));
        var length = Math.Min(take, text.Length - offsetChars);
        return text.Substring(offsetChars, length);
    }

    public async Task<IReadOnlyList<SessionTranscriptMatch>> SearchSessionTranscriptsAsync(
        string sessionId,
        string query,
        int limit,
        int previewChars,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(query))
        {
            return [];
        }

        limit = Math.Clamp(limit, 1, 20);
        previewChars = Math.Clamp(previewChars, 40, 500);
        var transcripts = await ListSessionTranscriptsAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var matches = new List<SessionTranscriptMatch>();
        foreach (var transcript in transcripts)
        {
            if (!TryResolveTranscriptPath(sessionId, transcript.FileName, out var path))
            {
                continue;
            }

            var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            var index = 0;
            while (matches.Count < limit
                   && (index = text.IndexOf(query, index, StringComparison.Ordinal)) >= 0)
            {
                var start = Math.Max(0, index - previewChars / 4);
                var length = Math.Min(previewChars, text.Length - start);
                matches.Add(new SessionTranscriptMatch(transcript.FileName, index, text.Substring(start, length)));
                index += query.Length;
            }

            if (matches.Count >= limit)
            {
                break;
            }
        }

        return matches;
    }

    public async Task<string> ReadHandoffNoteAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var path = GetHandoffNotePath(sessionId);
        if (!File.Exists(path))
        {
            return string.Empty;
        }

        return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> TryAppendHandoffNoteAsync(
        string sessionId,
        string text,
        CancellationToken cancellationToken = default)
    {
        var addition = text.Trim();
        if (addition.Length == 0)
        {
            return false;
        }

        using (await SessionWriteLock.AcquireAsync(sessionId, cancellationToken).ConfigureAwait(false))
        {
            var existing = await ReadHandoffNoteAsync(sessionId, cancellationToken).ConfigureAwait(false);
            var combined = string.IsNullOrWhiteSpace(existing)
                ? addition
                : existing.TrimEnd() + "\n" + addition;
            if (combined.Length > SessionHandoffNote.MaxChars)
            {
                return false;
            }

            var path = GetHandoffNotePath(sessionId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, combined, cancellationToken).ConfigureAwait(false);
            return true;
        }
    }

    private string GetSessionTranscriptsDirectory(string sessionId) =>
        Path.Combine(GetSessionDirectory(sessionId), "transcripts");

    private string GetHandoffNotePath(string sessionId) =>
        Path.Combine(GetSessionDirectory(sessionId), "notes", "handoff.md");

    private bool TryResolveTranscriptPath(string sessionId, string fileName, out string path)
    {
        path = string.Empty;
        if (!IsTranscriptFileName(fileName))
        {
            return false;
        }

        var transcriptDir = Path.GetFullPath(GetSessionTranscriptsDirectory(sessionId));
        var candidate = Path.GetFullPath(Path.Combine(transcriptDir, fileName));
        if (!candidate.StartsWith(transcriptDir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return false;
        }

        path = candidate;
        return true;
    }

    private static bool IsTranscriptFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)
            || fileName.Contains("..", StringComparison.Ordinal)
            || fileName.IndexOfAny(['/', '\\']) >= 0)
        {
            return false;
        }

        return fileName.StartsWith("transcript_", StringComparison.Ordinal)
            && fileName.EndsWith(".jsonl", StringComparison.Ordinal)
            && fileName.All(static c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.');
    }
}
