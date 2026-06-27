using System.Collections.Concurrent;
using System.Text;
using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure;

public sealed class GrepFilesTool(WorkspaceGuard guard, AuditLogService audit) : IAgentTool, IParallelizableAgentTool
{
    private const int MaxFilesToScan = 2000;
    private const int MaxMatches = 200;
    private const long MaxFileSizeBytes = 2 * 1024 * 1024;

    public ToolDefinition Definition { get; } = new(
        "grep_files",
        "Search file contents for a text pattern (literal by default; set regex to true for .NET regular expressions).",
        new Dictionary<string, string>
        {
            ["pattern"] = "Text pattern (literal or regex). Regex example with regex true: class\\s+\\w+",
            ["path"] = ToolPathDescriptions.OptionalWorkspaceRelativeDirectory,
            ["glob"] = "Optional file glob filter, e.g. *.cs",
            ["regex"] = "Optional true to treat pattern as .NET regular expression (default: false, literal case-insensitive)"
        });

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!ToolArguments.TryGetRequired(invocation, "pattern", out var pattern, out var error)) return error;
        if (!WorkspaceToolHelper.TryResolveOptionalNormalizedPath(invocation, guard, out var fullPath, out error)) return error;

        var useRegex = invocation.Arguments.TryGetValue("regex", out var regexValue)
            && bool.TryParse(regexValue, out var parsedRegex)
            && parsedRegex;
        if (!GrepLineMatcher.TryCreate(pattern, useRegex, out var matcher, out var regexError))
        {
            return ToolResult.Failure("Invalid regex", regexError ?? "Invalid pattern.");
        }

        var glob = invocation.Arguments.GetValueOrDefault("glob") ?? "*";
        var ignorePatterns = guard.GetIgnorePatterns();
        var baseRoot = guard.Normalize(".");
        var files = File.Exists(fullPath)
            ? new[] { fullPath }
            : Directory.Exists(fullPath)
                ? Directory.EnumerateFiles(fullPath, glob, SearchOption.AllDirectories)
                    .Where(file => !WorkspacePathFilter.ShouldIgnorePath(file, ignorePatterns))
                    .Take(MaxFilesToScan)
                    .ToArray()
                : Array.Empty<string>();

        var matchState = new GrepMatchState();
        var matches = new ConcurrentBag<string>();
        if (files.Length <= 1)
        {
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ScanFileAsync(file, matcher!, baseRoot, matches, matchState, cancellationToken).ConfigureAwait(false);
                if (matchState.IsFull)
                {
                    break;
                }
            }
        }
        else
        {
            await Parallel.ForEachAsync(
                files,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8),
                    CancellationToken = cancellationToken
                },
                async (file, ct) =>
                {
                    if (matchState.IsFull)
                    {
                        return;
                    }

                    await ScanFileAsync(file, matcher!, baseRoot, matches, matchState, ct).ConfigureAwait(false);
                }).ConfigureAwait(false);
        }

        var orderedMatches = matches.OrderBy(static line => line, StringComparer.Ordinal).ToArray();
        await WorkspaceToolHelper.AuditAsync(
            audit,
            "grep_files",
            new { path = fullPath, pattern, regex = useRegex, count = orderedMatches.Length },
            cancellationToken);
        return orderedMatches.Length == 0
            ? ToolResult.Success("No matches found", "No matches found")
            : ToolResult.Success($"Found {orderedMatches.Length} matches", string.Join(Environment.NewLine, orderedMatches));
    }

    private static async Task ScanFileAsync(
        string file,
        GrepLineMatcher.GrepLineMatcherInstance matcher,
        string baseRoot,
        ConcurrentBag<string> matches,
        GrepMatchState matchState,
        CancellationToken cancellationToken)
    {
        if (matchState.IsFull || !File.Exists(file))
        {
            return;
        }

        var fileInfo = new FileInfo(file);
        if (fileInfo.Length > MaxFileSizeBytes)
        {
            return;
        }

        try
        {
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 4096, useAsync: true);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var lineNumber = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (matchState.IsFull)
                {
                    return;
                }

                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                lineNumber++;
                if (!matcher.IsMatch(line))
                {
                    continue;
                }

                matchState.TryRecordMatch(matches, $"{Path.GetRelativePath(baseRoot, file)}:{lineNumber}:{line.Trim()}");
            }
        }
        catch (IOException)
        {
            // Skip transiently locked/unreadable files so grep continues instead of hanging.
        }
        catch (UnauthorizedAccessException)
        {
            // Skip files without access permissions.
        }
    }

    private sealed class GrepMatchState
    {
        private int _count;

        public bool IsFull => Volatile.Read(ref _count) >= MaxMatches;

        public bool TryRecordMatch(ConcurrentBag<string> matches, string formattedLine)
        {
            if (Volatile.Read(ref _count) >= MaxMatches)
            {
                return false;
            }

            var index = Interlocked.Increment(ref _count);
            if (index <= MaxMatches)
            {
                matches.Add(formattedLine);
                return true;
            }

            return false;
        }
    }
}
