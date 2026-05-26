using System.Text;
using System.Text.Json.Serialization;

namespace Athlon.Agent.Core;

public enum MessageRole
{
    System,
    User,
    Assistant,
    Tool,
    Summary
}

public sealed record ChatMessage(
    string Id,
    MessageRole Role,
    string Content,
    DateTimeOffset CreatedAt,
    string? ParentId = null)
{
    public static ChatMessage Create(MessageRole role, string content, string? parentId = null) =>
        new(Guid.NewGuid().ToString("N"), role, content, DateTimeOffset.UtcNow, parentId);
}

public sealed record AgentSession(
    string Id,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? ActiveWorkspace,
    string? ActiveSkill,
    string? ModelName,
    IReadOnlyList<ChatMessage> Messages)
{
    public static AgentSession Create(string title = "New chat") =>
        new(Guid.NewGuid().ToString("N"), title, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, null, Array.Empty<ChatMessage>());

    public AgentSession WithMessage(ChatMessage message) => this with
    {
        UpdatedAt = DateTimeOffset.UtcNow,
        Messages = Messages.Concat(new[] { message }).ToArray()
    };
}

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

public sealed record ContextSummary(string Id, string SessionId, string Content, int OriginalMessageCount, DateTimeOffset CreatedAt);

public sealed record SessionIndexEntry(string Id, string Title, string Path, DateTimeOffset UpdatedAt);

public sealed record AgentModelMessage(
    string Role,
    string Content,
    string? ToolCallId = null,
    IReadOnlyList<AgentToolCall>? ToolCalls = null);

public sealed record AgentToolCall(
    string Id,
    string Name,
    IReadOnlyDictionary<string, string> Arguments);

public sealed record AgentModelRequest(
    IReadOnlyList<AgentModelMessage> Messages,
    IReadOnlyList<ToolDefinition> Tools,
    bool AllowToolCalls = true);

public sealed record AgentModelResponse(
    string Content,
    IReadOnlyList<AgentToolCall> ToolCalls);

public interface IAgentModelClient
{
    Task<AgentModelResponse> CompleteAsync(AgentModelRequest request, CancellationToken cancellationToken = default);
}

public interface IAgentEnvironmentPromptBuilder
{
    string Build(AgentSession session, IReadOnlyList<ToolDefinition> tools);
}

public interface IAgentRuntime
{
    Task<AgentSession> SendAsync(AgentSession session, string userInput, Func<string, Task>? onToken = null, CancellationToken cancellationToken = default);
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

public interface IFileStorageService
{
    string RootPath { get; }
    Task SaveSessionAsync(AgentSession session, CancellationToken cancellationToken = default);
    Task SaveContextSummaryAsync(ContextSummary summary, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SessionIndexEntry>> ListSessionsAsync(CancellationToken cancellationToken = default);
    Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default);
    Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default);
}

public interface IAppLogger
{
    void Debug(string messageTemplate, params object[] values);
    void Information(string messageTemplate, params object[] values);
    void Warning(string messageTemplate, params object[] values);
    void Error(Exception exception, string messageTemplate, params object[] values);
    IAppLogger ForContext(string sourceContext);
}

public interface ICredentialStore
{
    Task SaveSecretAsync(string name, string secret, CancellationToken cancellationToken = default);
    Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken = default);
    Task<bool> HasSecretAsync(string name, CancellationToken cancellationToken = default);
}

public interface IAgentOrchestrator
{
    Task<AgentSession> SendAsync(AgentSession session, string userInput, Func<string, Task>? onToken = null, CancellationToken cancellationToken = default);
}

public sealed class AppSettings
{
    public ModelSettings Model { get; set; } = new();
    public LoggingSettings Logging { get; set; } = new();
    public ToolPermissionSettings ToolPermissions { get; set; } = new();
    public List<McpServerSettings> McpServers { get; set; } = new();
    public List<SkillSettings> Skills { get; set; } = new();
    public List<WorkspaceSettings> Workspaces { get; set; } = new();
    public UiSettings Ui { get; set; } = new();
}

public sealed class ModelSettings
{
    public const string ApiKeySecretName = "model-api-key";

    public string Provider { get; set; } = "OpenAI-Compatible";
    public string Endpoint { get; set; } = "https://api.openai.com/v1";
    public string ModelName { get; set; } = "gpt-4.1-mini";

    [JsonPropertyName("ApiKeyCredentialName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? LegacyApiKeyCredentialName { get; set; }
}

public sealed class LoggingSettings
{
    public string Directory { get; set; } = "";
    public string MinimumLevel { get; set; } = "Information";
    public int RetainedDays { get; set; } = 14;
    public long MaxFileSizeBytes { get; set; } = 10 * 1024 * 1024;
}

public sealed class ToolPermissionSettings
{
    public bool EnableCommandExecution { get; set; }
    public bool AskBeforeEveryCommand { get; set; } = true;
    public string FileScopePolicy { get; set; } = "AskOutsideWorkspace";
    public List<string> CommandAllowList { get; set; } = new() { "git", "dotnet", "python", "node", "npm" };
    public List<string> CommandDenyList { get; set; } = new() { "format", "del /s", "rmdir /s", "Remove-Item -Recurse" };
}

public sealed class McpServerSettings
{
    public string Name { get; set; } = "filesystem";
    public bool Enabled { get; set; }
    public string Command { get; set; } = "npx";
    public List<string> Args { get; set; } = new();
    public Dictionary<string, string> Env { get; set; } = new();
}

public sealed class SkillSettings
{
    public string Name { get; set; } = "data-cleaning";
    public bool Enabled { get; set; } = true;
    public string Path { get; set; } = ".codex/skills/data-cleaning.yaml";
}

public sealed class WorkspaceSettings
{
    public string Name { get; set; } = "Default";
    public string RootPath { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    public bool IsDefault { get; set; } = true;
    public List<string> IgnorePatterns { get; set; } = new() { ".git", "bin", "obj", "node_modules" };
}

public sealed class UiSettings
{
    public string Theme { get; set; } = "Dark";
    public double FontSize { get; set; } = 14;
    public double ContextSidebarWidth { get; set; } = 300;
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

public sealed class AgentEnvironmentPromptBuilder(AppSettings settings) : IAgentEnvironmentPromptBuilder
{
    public string Build(AgentSession session, IReadOnlyList<ToolDefinition> tools)
    {
        var builder = new StringBuilder();
        var workspace = GetActiveWorkspace();

        builder.AppendLine("You are Athlon Agent, a Windows desktop coding agent.");
        builder.AppendLine("Use the provided function tools when you need to inspect or modify workspace files. Do not guess file contents.");
        builder.AppendLine("All relative file paths are resolved from the active workspace. Never access files outside the configured workspace.");
        builder.AppendLine();

        if (workspace is null || string.IsNullOrWhiteSpace(workspace.RootPath))
        {
            builder.AppendLine("Active workspace: not configured.");
        }
        else
        {
            builder.AppendLine($"Active workspace: {workspace.Name}");
            builder.AppendLine($"Workspace root: {workspace.RootPath}");
            builder.AppendLine("Workspace contents are intentionally not embedded in this prompt because they change often.");
            builder.AppendLine("Use file_list to fetch a live directory listing when needed.");
        }

        builder.AppendLine();
        builder.AppendLine("Available native tools:");
        foreach (var tool in tools)
        {
            builder.AppendLine($"- {tool.Name}: {tool.Description}");
        }

        var mcpServers = settings.McpServers.Count == 0
            ? "No MCP servers configured."
            : string.Join(Environment.NewLine, settings.McpServers.Select(server => $"- {(server.Enabled ? "enabled" : "disabled")} {server.Name}: {server.Command} {string.Join(" ", server.Args)}"));

        builder.AppendLine();
        builder.AppendLine("MCP server status:");
        builder.AppendLine(mcpServers);
        builder.AppendLine();
        builder.AppendLine("When answering questions about files, call file_read first if the content is needed.");
        builder.AppendLine("When the user asks what files exist in the workspace or a directory, call file_list before answering.");
        builder.AppendLine("When searching file contents, call grep_files. When finding files by name or extension, call glob_files.");
        builder.AppendLine("For write operations, explain your intent before calling file_write or file_edit.");
        builder.AppendLine("Command execution may be disabled by settings; only call execute_command when it is necessary.");

        return builder.ToString();
    }

    private WorkspaceSettings? GetActiveWorkspace()
    {
        return settings.Workspaces.FirstOrDefault(workspace => workspace.IsDefault) ?? settings.Workspaces.FirstOrDefault();
    }

}

public sealed class AgentRuntime(IAgentModelClient modelClient, IFileStorageService storage, IToolRouter toolRouter, IAgentEnvironmentPromptBuilder promptBuilder, IAppLogger logger) : IAgentRuntime
{
    private const int MaxToolIterations = 6;
    private readonly IAppLogger _logger = logger.ForContext("AgentRuntime");

    public async Task<AgentSession> SendAsync(AgentSession session, string userInput, Func<string, Task>? onToken = null, CancellationToken cancellationToken = default)
    {
        var userMessage = ChatMessage.Create(MessageRole.User, userInput, session.Messages.LastOrDefault()?.Id);
        session = session.WithMessage(userMessage);
        var tools = toolRouter.ListTools();
        var modelMessages = BuildModelMessages(promptBuilder.Build(session, tools), session.Messages);

        if (ShouldListWorkspaceFiles(userInput) && tools.Any(tool => string.Equals(tool.Name, "file_list", StringComparison.OrdinalIgnoreCase)))
        {
            var toolCall = new AgentToolCall(Guid.NewGuid().ToString("N"), "file_list", new Dictionary<string, string>());
            var result = await toolRouter.InvokeAsync(new ToolInvocation(toolCall.Name, toolCall.Arguments), cancellationToken);
            var content = FormatToolResult(toolCall, result);
            session = session.WithMessage(ChatMessage.Create(MessageRole.Tool, content, userMessage.Id));
            modelMessages.Add(new AgentModelMessage("system", $"Preflight tool result for the user's file listing request:{Environment.NewLine}{content}"));
        }

        for (var iteration = 0; iteration < MaxToolIterations; iteration++)
        {
            var response = await modelClient.CompleteAsync(new AgentModelRequest(modelMessages, tools), cancellationToken);
            if (response.ToolCalls.Count == 0)
            {
                if (onToken is not null && !string.IsNullOrEmpty(response.Content))
                {
                    await onToken(response.Content);
                }

                var assistant = ChatMessage.Create(MessageRole.Assistant, response.Content, userMessage.Id);
                session = session.WithMessage(assistant);
                await storage.SaveSessionAsync(session, cancellationToken);
                _logger.Information("Saved session {SessionId} with {MessageCount} messages", session.Id, session.Messages.Count);
                return session;
            }

            modelMessages.Add(new AgentModelMessage("assistant", response.Content, ToolCalls: response.ToolCalls));
            foreach (var toolCall in response.ToolCalls)
            {
                var result = await toolRouter.InvokeAsync(new ToolInvocation(toolCall.Name, toolCall.Arguments), cancellationToken);
                var content = FormatToolResult(toolCall, result);
                session = session.WithMessage(ChatMessage.Create(MessageRole.Tool, content, userMessage.Id));
                modelMessages.Add(new AgentModelMessage("tool", content, toolCall.Id));
            }
        }

        var limitMessage = ChatMessage.Create(MessageRole.Assistant, "工具调用次数已达到上限，我已停止继续执行。", userMessage.Id);
        session = session.WithMessage(limitMessage);
        await storage.SaveSessionAsync(session, cancellationToken);
        return session;
    }

    private static List<AgentModelMessage> BuildModelMessages(string environmentPrompt, IReadOnlyList<ChatMessage> history)
    {
        var messages = new List<AgentModelMessage>
        {
            new("system", environmentPrompt)
        };

        foreach (var message in history)
        {
            messages.Add(message.Role switch
            {
                MessageRole.User => new AgentModelMessage("user", message.Content),
                MessageRole.Assistant => new AgentModelMessage("assistant", message.Content),
                MessageRole.Tool => new AgentModelMessage("system", $"Previous tool result: {message.Content}"),
                MessageRole.Summary => new AgentModelMessage("system", $"History summary: {message.Content}"),
                _ => new AgentModelMessage("system", message.Content)
            });
        }

        return messages;
    }

    private static string FormatToolResult(AgentToolCall call, ToolResult result)
    {
        var status = result.Succeeded ? "succeeded" : "failed";
        return string.Join(Environment.NewLine, new[]
        {
            $"Tool `{call.Name}` {status}.",
            "",
            $"Arguments: {FormatArguments(call.Arguments)}",
            $"Summary: {result.Summary}",
            "",
            result.Content ?? result.Error ?? string.Empty
        });
    }

    private static string FormatArguments(IReadOnlyDictionary<string, string> arguments)
    {
        return arguments.Count == 0
            ? "(none)"
            : string.Join("; ", arguments.Select(argument => $"{argument.Key}={argument.Value}"));
    }

    private static bool ShouldListWorkspaceFiles(string userInput)
    {
        var input = userInput.Trim();
        return ContainsAny(input, "有哪些文件", "什么文件", "文件列表", "目录下", "目录里", "工作区文件", "list files", "what files", "which files");
    }

    private static bool ContainsAny(string value, params string[] terms)
    {
        return terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class AgentOrchestrator(IAgentRuntime agentRuntime) : IAgentOrchestrator
{
    public Task<AgentSession> SendAsync(AgentSession session, string userInput, Func<string, Task>? onToken = null, CancellationToken cancellationToken = default) =>
        agentRuntime.SendAsync(session, userInput, onToken, cancellationToken);
}

public sealed class ContextCompressionService
{
    public ContextSummary CreateSummary(string sessionId, IReadOnlyList<ChatMessage> messages)
    {
        var middle = messages.Skip(2).Take(Math.Max(0, messages.Count - 6)).ToArray();
        var content = middle.Length == 0
            ? "No middle history required compression."
            : string.Join(Environment.NewLine, middle.Select(message => $"- {message.Role}: {Trim(message.Content, 180)}"));

        return new ContextSummary(Guid.NewGuid().ToString("N"), sessionId, content, middle.Length, DateTimeOffset.UtcNow);
    }

    private static string Trim(string value, int length) => value.Length <= length ? value : value[..length] + "...";
}
