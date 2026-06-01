using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Athlon.Agent.Infrastructure;

public sealed class GrepFilesTool(WorkspaceGuard guard, AuditLogService audit) : IAgentTool
{
    private const int MaxFilesToScan = 2000;
    private const int MaxMatches = 200;
    private const long MaxFileSizeBytes = 2 * 1024 * 1024;

    public ToolDefinition Definition { get; } = new(
        "grep_files",
        "Search workspace file contents for a literal text pattern.",
        new Dictionary<string, string>
        {
            ["pattern"] = "Literal text pattern to search for",
            ["path"] = ToolPathDescriptions.OptionalWorkspaceRelativeDirectory,
            ["glob"] = "Optional file glob filter, e.g. *.cs"
        });

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!ToolArguments.TryGetRequired(invocation, "pattern", out var pattern, out var error)) return error;
        if (!ToolArguments.TryGetOptionalNormalizedPath(invocation, out var requestedPath, out error)) return error;
        var fullPath = guard.Normalize(requestedPath);
        if (!guard.IsInsideWorkspace(fullPath)) return ToolResult.Failure("Outside workspace", fullPath);

        var glob = invocation.Arguments.GetValueOrDefault("glob") ?? "*";
        var ignorePatterns = guard.GetIgnorePatterns();
        var baseRoot = guard.Normalize(".");
        var files = File.Exists(fullPath)
            ? new[] { fullPath }
            : Directory.Exists(fullPath)
                ? Directory.EnumerateFiles(fullPath, glob, SearchOption.AllDirectories)
                    .Where(file => !ShouldIgnore(file, ignorePatterns))
                    .Take(MaxFilesToScan)
                : Array.Empty<string>();

        var matches = new List<string>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(file))
            {
                continue;
            }

            var fileInfo = new FileInfo(file);
            if (fileInfo.Length > MaxFileSizeBytes)
            {
                continue;
            }

            try
            {
                await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 4096, useAsync: true);
                using var reader = new StreamReader(stream);
                var lineNumber = 0;
                while (!reader.EndOfStream)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var line = await reader.ReadLineAsync(cancellationToken) ?? string.Empty;
                    lineNumber++;
                    if (!line.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    matches.Add($"{Path.GetRelativePath(baseRoot, file)}:{lineNumber}:{line.Trim()}");
                    if (matches.Count >= MaxMatches)
                    {
                        break;
                    }
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

            if (matches.Count >= MaxMatches) break;
        }

        await audit.WriteAsync("grep_files", new { path = fullPath, pattern, count = matches.Count }, cancellationToken);
        return matches.Count == 0
            ? ToolResult.Success("No matches found", "No matches found")
            : ToolResult.Success($"Found {matches.Count} matches", string.Join(Environment.NewLine, matches));
    }

    private static bool ShouldIgnore(string fullPath, IReadOnlyList<string> ignorePatterns)
    {
        foreach (var segment in fullPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (ignorePatterns.Any(pattern => string.Equals(pattern, segment, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }
}
