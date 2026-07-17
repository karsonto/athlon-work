using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;

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
        "Search file contents for a text pattern (literal by default; set regex to true for .NET regular expressions).",
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
        var workspaceRoot = guard.Normalize(".");
        var matches = new List<string>();
        var scanned = 0;

        try
        {
            await foreach (var file in EnumerateFilesAsync(fullPath, glob, ignorePatterns, cancellationToken).ConfigureAwait(false))
            {
                scanned++;
                if (scanned > MaxFilesToScan || matches.Count >= MaxMatches)
                {
                    break;
                }

                SshFileInfo info;
                try
                {
                    info = await client.GetFileInfoAsync(file, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    continue;
                }

                if (info.IsDirectory || info.Length > MaxFileSizeBytes)
                {
                    continue;
                }

                string text;
                try
                {
                    text = await client.ReadTextAsync(file, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    continue;
                }

                var relative = SshWorkspaceToolHelper.ToRelative(workspaceRoot, file);
                var lineNumber = 0;
                using var reader = new StringReader(text);
                while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                {
                    lineNumber++;
                    if (!matcher.IsMatch(line))
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
                new { path = fullPath, pattern, count = matches.Count, remote = true },
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

    private async IAsyncEnumerable<string> EnumerateFilesAsync(
        string root,
        string glob,
        IReadOnlyList<string> ignorePatterns,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!await client.FileExistsAsync(root, cancellationToken).ConfigureAwait(false))
        {
            yield break;
        }

        var info = await client.GetFileInfoAsync(root, cancellationToken).ConfigureAwait(false);
        if (!info.IsDirectory)
        {
            if (MatchesGlob(RemotePathNormalizer.GetFileName(root), glob))
            {
                yield return root;
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

                if (!MatchesGlob(entry.Name, glob))
                {
                    continue;
                }

                yielded++;
                yield return entry.FullPath;
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
        // FileSystemGlobbing expects relative paths; match against the file name only.
        var result = matcher.Match(fileName);
        if (result.HasMatches)
        {
            return true;
        }

        // Also allow patterns like **/*.cs by matching against a fake relative path.
        result = matcher.Match(".", new[] { fileName });
        return result.HasMatches;
    }
}
