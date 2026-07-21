namespace Athlon.Agent.Core;

public sealed record ToolDefinition(
    string Name,
    string Description,
    ToolJsonSchema ParametersSchema,
    bool RequiresApproval = false,
    string Source = "native",
    ToolGroup Group = ToolGroup.Builtin,
    int? MaxOutputChars = null,
    ToolInvocationPolicy InvocationPolicy = ToolInvocationPolicy.Allow);

public sealed record ToolInvocation(
    string ToolName,
    ToolCallArguments Arguments,
    string? Explanation = null,
    ToolApprovalDecision ApprovalDecision = ToolApprovalDecision.None,
    bool SkipValidation = false)
{
    public ToolInvocation(
        string toolName,
        IReadOnlyDictionary<string, string> arguments,
        string? explanation = null)
        : this(toolName, ToolCallArguments.FromStrings(arguments), explanation, ToolApprovalDecision.None)
    {
    }
}

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

    ToolDefinition? FindDefinition(string name) =>
        ListTools().FirstOrDefault(tool => string.Equals(tool.Name, name, StringComparison.OrdinalIgnoreCase));

    bool IsParallelizable(string toolName) => false;
}

public sealed class ToolRouter(IEnumerable<IAgentTool> tools) : IToolRouter
{
    private readonly Dictionary<string, IAgentTool> _tools = tools.ToDictionary(tool => tool.Definition.Name, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ToolDefinition> ListTools() => _tools.Values.Select(tool => tool.Definition).OrderBy(tool => tool.Name).ToArray();

    public ToolDefinition? FindDefinition(string name) =>
        _tools.TryGetValue(name, out var tool) ? tool.Definition : null;

    public bool IsParallelizable(string toolName) =>
        _tools.TryGetValue(toolName, out var tool) && tool is IParallelizableAgentTool;

    public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!_tools.TryGetValue(invocation.ToolName, out var tool))
        {
            return Task.FromResult(ToolResult.Failure("Tool not found", $"No tool named '{invocation.ToolName}' is registered."));
        }

        if (!invocation.SkipValidation)
        {
            var validationError = ToolInvocationValidator.Validate(tool.Definition.ParametersSchema, invocation.Arguments);
            if (validationError is not null)
            {
                return Task.FromResult(ToolInvocationErrors.Failure("Invalid tool arguments", validationError));
            }

            var blocked = ToolInvocationPolicyEnforcer.TryBlockInvocation(
                tool.Definition,
                invocation.ApprovalDecision);
            if (blocked is not null)
            {
                return Task.FromResult(blocked);
            }
        }

        return tool.InvokeAsync(invocation, cancellationToken);
    }
}
