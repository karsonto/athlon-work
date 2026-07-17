using System.Text;
using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure;

public sealed class GrepFilesTool(WorkspaceGuard guard, AuditLogService audit, AppSettings settings) : IAgentTool, IParallelizableAgentTool, ILocalWorkspaceTool
{
    private const int MaxFilesToScan = 2000;
    private const int MaxMatches = 200;
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
        if (!ToolArguments.TryGetRequired(invocation, "pattern", out var pattern, out var error)) return error;
        if (!WorkspaceToolHelper.TryResolveOptionalNormalizedPath(invocation, guard, out var fullPath, out error)) return error;

        var useRegex = invocation.Arguments.GetBoolean("regex");
        if (!GrepLineMatcher.TryCreate(pattern, useRegex, out var matcher, out var regexError))
        {
            return ToolResult.Failure("Invalid regex", regexError ?? "Invalid pattern.");
        }

        var glob = invocation.Arguments.GetString("glob") ?? "*";
        var ignorePatterns = guard.GetIgnorePatterns();
        var baseRoot = guard.Normalize(".");
        var allFiles = File.Exists(fullPath)
            ? new[] { fullPath }
            : Directory.Exists(fullPath)
                ? Directory.EnumerateFiles(fullPath, glob, SearchOption.AllDirectories)
                    .Where(file => !WorkspacePathFilter.ShouldIgnorePath(file, ignorePatterns))
                    .Take(MaxFilesToScan)
                    .ToArray()
                : Array.Empty<string>();

        var matches = new List<string>();
        var matchCount = 0;
        var matchLock = new object();

        // Compute reasonable parallelism: avoid over-subscription on small file sets
        var maxDegree = allFiles.Length < 8
            ? 1
            : settings.ParallelToolExecution.MaxDegreeOfParallelism;
        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = maxDegree
        };

        await Parallel.ForEachAsync(allFiles, parallelOptions, async (file, ct) =>
        {
            // Early exit: enough matches found
            if (Volatile.Read(ref matchCount) >= MaxMatches)
                return;

            if (!File.Exists(file))
                return;

            var fileInfo = new FileInfo(file);
            if (fileInfo.Length > MaxFileSizeBytes)
                return;

            try
            {
                await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 4096, useAsync: true);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var lineNumber = 0;
                string? line;
                while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
                {
                    ct.ThrowIfCancellationRequested();

                    if (Volatile.Read(ref matchCount) >= MaxMatches)
                        return;

                    lineNumber++;
                    if (!matcher!.IsMatch(line))
                        continue;

                    var matchLine = $"{Path.GetRelativePath(baseRoot, file)}:{lineNumber}:{line.Trim()}";
                    lock (matchLock)
                    {
                        if (matchCount < MaxMatches)
                        {
                            matches.Add(matchLine);
                            matchCount++;
                        }
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
        }).ConfigureAwait(false);

        await WorkspaceToolHelper.AuditAsync(audit, "grep_files", new { path = fullPath, pattern, regex = useRegex, count = matches.Count }, cancellationToken);
        return matches.Count == 0
            ? ToolResult.Success("No matches found", "No matches found")
            : ToolResult.Success($"Found {matches.Count} matches", string.Join(Environment.NewLine, matches));
    }
}
