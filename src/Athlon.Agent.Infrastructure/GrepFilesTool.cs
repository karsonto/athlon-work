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
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var lineNumber = 0;
                while (!reader.EndOfStream)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) ?? string.Empty;
                    lineNumber++;
                    if (!matcher!.IsMatch(line))
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

            if (matches.Count >= MaxMatches)
            {
                break;
            }
        }

        await WorkspaceToolHelper.AuditAsync(audit, "grep_files", new { path = fullPath, pattern, regex = useRegex, count = matches.Count }, cancellationToken);
        return matches.Count == 0
            ? ToolResult.Success("No matches found", "No matches found")
            : ToolResult.Success($"Found {matches.Count} matches", string.Join(Environment.NewLine, matches));
    }
}
