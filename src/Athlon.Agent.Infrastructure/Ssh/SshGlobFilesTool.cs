using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;
using Microsoft.Extensions.FileSystemGlobbing;

namespace Athlon.Agent.Infrastructure.Ssh;

public sealed class SshGlobFilesTool(
    WorkspaceGuard guard,
    ISshWorkspaceClient client,
    AuditLogService audit) : IAgentTool, IRemoteWorkspaceTool, IParallelizableAgentTool
{
    private const int MaxFiles = 500;
    private const int MaxDepth = 12;

    public ToolDefinition Definition { get; } = new(
        "glob_files",
        "Find files matching a glob pattern.",
        ToolSchema.Object()
            .String("pattern", "Glob pattern (supports ** and {a,b} extensions), e.g. **/*.cs or **/*.{png,jpg}", required: true, minLength: 1)
            .String("path", ToolPathDescriptions.OptionalWorkspaceRelativeDirectory)
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

        try
        {
            if (!await client.FileExistsAsync(fullPath, cancellationToken).ConfigureAwait(false))
            {
                return ToolResult.Failure("Directory not found", fullPath);
            }

            var info = await client.GetFileInfoAsync(fullPath, cancellationToken).ConfigureAwait(false);
            if (!info.IsDirectory)
            {
                return ToolResult.Failure("Directory not found", fullPath);
            }

            var ignorePatterns = guard.GetIgnorePatterns();
            var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
            matcher.AddInclude(pattern.Replace('\\', '/'));
            var matches = new List<string>();
            var queue = new Queue<(string Path, string Relative, int Depth)>();
            queue.Enqueue((fullPath, string.Empty, 0));

            while (queue.Count > 0 && matches.Count < MaxFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (current, relative, depth) = queue.Dequeue();
                await foreach (var entry in client.ListAsync(current, cancellationToken).ConfigureAwait(false))
                {
                    if (SshWorkspaceToolHelper.ShouldIgnore(entry.FullPath, ignorePatterns))
                    {
                        continue;
                    }

                    var childRelative = string.IsNullOrEmpty(relative)
                        ? entry.Name
                        : relative + "/" + entry.Name;

                    if (entry.IsDirectory)
                    {
                        if (depth < MaxDepth)
                        {
                            queue.Enqueue((entry.FullPath, childRelative, depth + 1));
                        }

                        if (matcher.Match(childRelative + "/").HasMatches || matcher.Match(childRelative).HasMatches)
                        {
                            matches.Add(childRelative + "/");
                        }

                        continue;
                    }

                    if (matcher.Match(childRelative).HasMatches)
                    {
                        matches.Add($"{childRelative} ({entry.Length} bytes)");
                    }

                    if (matches.Count >= MaxFiles)
                    {
                        break;
                    }
                }
            }

            await WorkspaceToolHelper.AuditAsync(
                audit,
                "glob_files",
                new { path = fullPath, pattern, count = matches.Count, remote = true },
                cancellationToken).ConfigureAwait(false);

            return matches.Count == 0
                ? ToolResult.Success("No matching files found", "No matching files found")
                : ToolResult.Success($"Found {matches.Count} matching entries", string.Join(Environment.NewLine, matches));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ToolResult.Failure("Glob failed", ex.Message);
        }
    }
}
