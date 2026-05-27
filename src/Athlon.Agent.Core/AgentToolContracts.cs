using System.Diagnostics;
using System.Text;
using System.Text.Json.Serialization;
using Athlon.Agent.Core.Compaction;

namespace Athlon.Agent.Core;

public sealed record ToolDefinition(
    string Name,
    string Description,
    IReadOnlyDictionary<string, string> Parameters,
    bool RequiresApproval = false,
    string Source = "native");
public sealed record ToolInvocation(string ToolName, IReadOnlyDictionary<string, string> Arguments, string? Explanation = null);
public sealed record ToolResult(bool Succeeded, string Summary, string? Content = null, string? Error = null, TimeSpan? Duration = null)
{
    public static ToolResult Success(string summary, string? content = null, TimeSpan? duration = null) => new(true, summary, content, null, duration);
    public static ToolResult Failure(string summary, string error, TimeSpan? duration = null) => new(false, summary, null, error, duration);
}
public interface IAgentTool
{
    ToolDefinition Definition { get; }
    Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default);
}
public interface IToolRouter
{
    IReadOnlyList<ToolDefinition> ListTools();
    Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default);
}
public sealed class ToolRouter(IEnumerable<IAgentTool> tools) : IToolRouter
{
    private readonly Dictionary<string, IAgentTool> _tools = tools.ToDictionary(tool => tool.Definition.Name, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ToolDefinition> ListTools() => _tools.Values.Select(tool => tool.Definition).OrderBy(tool => tool.Name).ToArray();

    public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!_tools.TryGetValue(invocation.ToolName, out var tool))
        {
            return Task.FromResult(ToolResult.Failure("Tool not found", $"No tool named '{invocation.ToolName}' is registered."));
        }

        return tool.InvokeAsync(invocation, cancellationToken);
    }
}
public static class CompressTool
{
    public const string ToolName = "compress";
}
