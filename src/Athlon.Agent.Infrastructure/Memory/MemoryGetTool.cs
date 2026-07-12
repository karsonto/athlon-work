using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Memory;

namespace Athlon.Agent.Infrastructure.Memory;

public sealed class MemoryGetTool(ILongTermMemory longTermMemory, IAppLogger logger) : IAgentTool, ILongTermMemoryTool
{
    private readonly IAppLogger _logger = logger.ForContext("MemoryGetTool");

    public ToolDefinition Definition => new(
        Name: "memory_get",
        Description: "Read specific lines from a memory file. Use after memory_search to pull full context around matched lines. Path is relative to memory directory (e.g., MEMORY.md or 2026-04-01.md).",
        ToolSchema.Object()
            .String("path", "Relative path to the memory file (e.g., MEMORY.md or 2026-04-01.md)", required: true, minLength: 1)
            .Integer("start_line", "Start line number (1-based, inclusive)", required: true, minimum: 1)
            .Integer("end_line", "End line number (1-based, inclusive)", required: true, minimum: 1)
            .Build());

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!invocation.Arguments.TryGetString("path", out var path) || string.IsNullOrWhiteSpace(path))
            return ToolResult.Failure("Missing path", "path parameter is required");

        if (!invocation.Arguments.TryGetInt32("start_line", out var startLine))
            return ToolResult.Failure("Missing or invalid start_line", "start_line must be an integer");

        if (!invocation.Arguments.TryGetInt32("end_line", out var endLine))
            return ToolResult.Failure("Missing or invalid end_line", "end_line must be an integer");

        try
        {
            string content;
            if (path.Equals("MEMORY.md", StringComparison.OrdinalIgnoreCase))
            {
                content = await longTermMemory.ReadCuratedAsync(cancellationToken);
            }
            else
            {
                var fileName = path;
                // Trim "memory/" prefix if present
                if (fileName.StartsWith("memory/", StringComparison.OrdinalIgnoreCase) ||
                    fileName.StartsWith("memory\\", StringComparison.OrdinalIgnoreCase))
                {
                    fileName = fileName.Split('/', '\\')[^1];
                }
                content = await longTermMemory.ReadDailyFileAsync(fileName, cancellationToken);
            }

            if (string.IsNullOrEmpty(content))
                return ToolResult.Failure("File not found", $"No memory file found at: {path}");

            var lines = content.Split('\n');
            var start = Math.Max(0, startLine - 1);
            var end = Math.Min(lines.Length, endLine);

            if (start >= lines.Length)
                return ToolResult.Failure("Invalid range",
                    $"start_line {startLine} exceeds file length {lines.Length}");

            var sb = new StringBuilder();
            for (int i = start; i < end; i++)
            {
                sb.AppendLine($"{i + 1}|{lines[i]}");
            }
            return ToolResult.Success($"Read lines {startLine}-{endLine} from {path}", sb.ToString());
        }
        catch (Exception ex)
        {
            _logger.Warning("Memory get failed: {Error}", ex.Message);
            return ToolResult.Failure("Read failed", ex.Message);
        }
    }
}
