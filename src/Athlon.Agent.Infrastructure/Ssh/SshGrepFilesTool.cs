using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;
using Microsoft.Extensions.FileSystemGlobbing;

namespace Athlon.Agent.Infrastructure.Ssh;

public sealed class SshGrepFilesTool(
    WorkspaceGuard guard,
    ISshWorkspaceClient client,
    AuditLogService audit) : IAgentTool, IRemoteWorkspaceTool, IParallelizableAgentTool
{
    private const int MaxFilesToScan = 500;
    private const int MaxMatches = 200;
    private const int MaxDepth = 12;
    private const long MaxFileSizeBytes = 2 * 1024 * 1024;

    public ToolDefinition Definition { get; } = new(
        "grep_files",
        "Search file contents for a text pattern (literal by default; set regex to true for .NET regular expressions). "
            + $"Search is case-insensitive by default. Scans up to {MaxFilesToScan} files, returns up to {MaxMatches} matches.",
        ToolSchema.Object()
            .String("pattern", "Text pattern (literal or regex). Regex example with regex true: class\\s+\\w+", required: true, minLength: 1)
            .String("path", ToolPathDescriptions.OptionalWorkspaceRelativeDirectory)
            .String("glob", "File glob filter, e.g. *.cs", defaultValue: "*", minLength: 1)
            .Boolean("regex", "Treat pattern as .NET regular expression (default: false, literal case-insensitive)", defaultValue: false)
            .Build());

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!ToolArguments.TryGetRequired(invocation, "pattern", out var pattern, out var error))
        {
            return error;
        }

        if (!SshWorkspaceToolHelper.TryResolveOptionalNormalizedPath(invocation, guard, client, out var fullPath, out error))
        {
            return error;
        }

        var useRegex = invocation.Arguments.GetBoolean("regex");
        if (!GrepLineMatcher.TryCreate(pattern, useRegex, out var matcher, out var regexError))
        {
            return ToolResult.Failure("Invalid regex", regexError ?? "Invalid pattern.");
        }

        var glob = invocation.Arguments.GetString("glob") ?? "*";
        var ignorePatterns = guard.GetIgnorePatterns();

        try
        {
            var info = await client.TryGetFileInfoAsync(fullPath, cancellationToken).ConfigureAwait(false);
            if (info is null)
            {
                return ToolResult.Success("No matches found", "No matches found");
            }

            // Prefer remote rg/grep to avoid downloading every file over SFTP.
            if (info.IsDirectory)
            {
                var remoteMatches = await SshRemoteSearch.TryGrepAsync(
                        client,
                        fullPath,
                        pattern,
                        glob,
                        useRegex,
                        MaxMatches,
                        ignorePatterns,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (remoteMatches is not null)
                {
                    await WorkspaceToolHelper.AuditAsync(
                        audit,
                        "grep_files",
                        new { path = SshWorkspaceToolHelper.ToAuditPath(guard, fullPath), pattern, count = remoteMatches.Count, remote = true, via = "shell" },
                        cancellationToken).ConfigureAwait(false);

                    return remoteMatches.Count == 0
                        ? ToolResult.Success("No matches found", "No matches found")
                        : ToolResult.Success(
                            $"Found {remoteMatches.Count} match(es)",
                            string.Join(Environment.NewLine, remoteMatches));
                }
            }

            var matches = new List<string>();
            var scanned = 0;
            var workspaceRoot = guard.Normalize(".");

            await foreach (var file in EnumerateFilesAsync(fullPath, info, glob, ignorePatterns, cancellationToken)
                               .ConfigureAwait(false))
            {
                scanned++;
                if (scanned > MaxFilesToScan || matches.Count >= MaxMatches)
                {
                    break;
                }

                string text;
                try
                {
                    text = await client.ReadTextAsync(file.FullPath, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    continue;
                }

                var relative = SshWorkspaceToolHelper.ToRelative(workspaceRoot, file.FullPath);
                var lineNumber = 0;
                using var reader = new StringReader(text);
                string? line;
                while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
                {
                    lineNumber++;
                    if (matcher is null || !matcher.IsMatch(line))
                    {
                        continue;
                    }

                    matches.Add($"{relative}:{lineNumber}:{line}");
                    if (matches.Count >= MaxMatches)
                    {
                        break;
                    }
                }
            }

            await WorkspaceToolHelper.AuditAsync(
                audit,
                "grep_files",
                new { path = SshWorkspaceToolHelper.ToAuditPath(guard, fullPath), pattern, count = matches.Count, remote = true, via = "sftp" },
                cancellationToken).ConfigureAwait(false);

            return matches.Count == 0
                ? ToolResult.Success("No matches found", "No matches found")
                : ToolResult.Success($"Found {matches.Count} match(es)", string.Join(Environment.NewLine, matches));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ToolResult.Failure("Grep failed", ex.Message);
        }
    }

    private async IAsyncEnumerable<SshEntry> EnumerateFilesAsync(
        string root,
        SshFileInfo rootInfo,
        string glob,
        IReadOnlyList<string> ignorePatterns,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!rootInfo.IsDirectory)
        {
            if (rootInfo.Length <= MaxFileSizeBytes
                && MatchesGlob(RemotePathNormalizer.GetFileName(root), glob))
            {
                yield return new SshEntry(RemotePathNormalizer.GetFileName(root), root, false, rootInfo.Length);
            }

            yield break;
        }

        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));
        var yielded = 0;
        while (queue.Count > 0 && yielded < MaxFilesToScan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (current, depth) = queue.Dequeue();
            await foreach (var entry in client.ListAsync(current, cancellationToken).ConfigureAwait(false))
            {
                if (SshWorkspaceToolHelper.ShouldIgnore(entry.FullPath, ignorePatterns))
                {
                    continue;
                }

                if (entry.IsDirectory)
                {
                    if (depth < MaxDepth)
                    {
                        queue.Enqueue((entry.FullPath, depth + 1));
                    }

                    continue;
                }

                if (entry.Length > MaxFileSizeBytes || !MatchesGlob(entry.Name, glob))
                {
                    continue;
                }

                yielded++;
                yield return entry;
                if (yielded >= MaxFilesToScan)
                {
                    yield break;
                }
            }
        }
    }

    private static bool MatchesGlob(string fileName, string glob)
    {
        if (string.IsNullOrWhiteSpace(glob) || glob == "*")
        {
            return true;
        }

        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddInclude(glob.Replace('\\', '/'));
        if (matcher.Match(fileName).HasMatches)
        {
            return true;
        }

        return matcher.Match(".", new[] { fileName }).HasMatches;
    }
}
