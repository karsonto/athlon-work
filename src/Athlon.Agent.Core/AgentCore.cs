using System.Diagnostics;
using System.Text;
using System.Text.Json.Serialization;
using Athlon.Agent.Core.Compaction;

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

    public AgentSession WithWorkspace(string? workspaceRootPath) => this with
    {
        ActiveWorkspace = workspaceRootPath,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    public AgentSession WithTitle(string title) => this with
    {
        Title = title,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    public AgentSession WithMessages(IReadOnlyList<ChatMessage> messages) => this with
    {
        UpdatedAt = DateTimeOffset.UtcNow,
        Messages = messages.ToArray()
    };
}

public interface IActiveWorkspaceContext
{
    string? RootPath { get; }
    string? DisplayName { get; }
    IReadOnlyList<string> IgnorePatterns { get; }
    void SetWorkspace(string? rootPath, string? displayName = null, IReadOnlyList<string>? ignorePatterns = null);
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
    bool AllowToolCalls = true,
    int? MaxTokens = null);

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

/// <summary>Current Windows desktop host facts injected into the agent environment prompt.</summary>
public interface IAgentHostEnvironment
{
    bool IsWindows { get; }
    string OsDescription { get; }
    string OsVersion { get; }
    string UserName { get; }
    string UserDomainName { get; }
    string MachineName { get; }
    string UserProfilePath { get; }
    string CurrentDirectory { get; }
    string SystemDirectory { get; }
    string ProcessArchitecture { get; }
    string OsArchitecture { get; }
    int ProcessorCount { get; }
    string AppDataDirectory { get; }
    string SkillsDirectory { get; }
}

public sealed record AvailableSkillInfo(string Name, string Description, string SkillId);

public interface IAvailableSkillsProvider
{
    IReadOnlyList<AvailableSkillInfo> GetSkills();
}

public interface IAgentRuntime
{
    Task<AgentSession> SendAsync(
        AgentSession session,
        string userInput,
        AgentTurnCallbacks? callbacks = null,
        CancellationToken cancellationToken = default);
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
    Task<AgentSession?> LoadSessionAsync(string sessionId, CancellationToken cancellationToken = default);
    Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default);
    Task SaveContextSummaryAsync(ContextSummary summary, CancellationToken cancellationToken = default);
    Task<string> SaveTranscriptAsync(string sessionId, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default);
    Task AppendConversationMessageAsync(string sessionId, ChatMessage message, CancellationToken cancellationToken = default);
    Task AppendToolCallLogAsync(string sessionId, SessionToolCallLogEntry entry, CancellationToken cancellationToken = default);
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
    Task<AgentSession> SendAsync(
        AgentSession session,
        string userInput,
        AgentTurnCallbacks? callbacks = null,
        CancellationToken cancellationToken = default);
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
    public ContextCompactionSettings ContextCompaction { get; set; } = new();
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
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;

    /// <summary>Skill folder name under <c>~/.athlon-agent/skills</c> (optional override).</summary>
    public string Path { get; set; } = string.Empty;
}

public sealed class WorkspaceSettings
{
    public string Name { get; set; } = string.Empty;
    public string RootPath { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
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

public sealed class AgentEnvironmentPromptBuilder(
    AppSettings settings,
    IAvailableSkillsProvider skillsProvider,
    IAgentHostEnvironment host) : IAgentEnvironmentPromptBuilder
{
    public string Build(AgentSession session, IReadOnlyList<ToolDefinition> tools)
    {
        var builder = new StringBuilder();
        var workspace = ResolveWorkspace(session);

        builder.AppendLine("You are Athlon Agent, a Windows desktop coding agent.");
        builder.AppendLine("Use the provided function tools when you need to inspect or modify workspace files. Do not guess file contents.");
        builder.AppendLine();
        AppendHostEnvironment(builder);
        builder.AppendLine();

        if (workspace is null || string.IsNullOrWhiteSpace(workspace.RootPath))
        {
            builder.AppendLine("当前工作区尚未设定。");
            builder.AppendLine("请让用户通过侧栏「配置」或设置页的 Workspace 指定工作区目录后，再使用 file_list、file_read、file_write、file_edit、grep_files、glob_files 等文件工具。");
            builder.AppendLine("在工作区未设定前，不要假设任何文件路径，也不要调用访问工作区文件的工具。");
        }
        else
        {
            builder.AppendLine("All relative file paths are resolved from the active workspace. Never access files outside the configured workspace.");
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

        var skills = skillsProvider.GetSkills();
        builder.AppendLine();
        if (skills.Count == 0)
        {
            builder.AppendLine($"Available skills: none installed under {host.SkillsDirectory}.");
            builder.AppendLine($"Install skills as <skill-name>/SKILL.md under {host.SkillsDirectory} with YAML frontmatter (name, description).");
        }
        else
        {
            builder.AppendLine($"Available skills (each folder under {host.SkillsDirectory}):");
            foreach (var skill in skills)
            {
                builder.AppendLine($"- {skill.Name}: {skill.Description} (skill-id: {skill.SkillId})");
            }

            builder.AppendLine("When a skill matches the task, follow instructions in its SKILL.md content.");
        }

        builder.AppendLine();
        builder.AppendLine("When answering questions about files, call file_read first if the content is needed.");
        builder.AppendLine("When the user asks what files exist in the workspace or a directory, call file_list before answering.");
        builder.AppendLine("When searching file contents, call grep_files. When finding files by name or extension, call glob_files.");
        builder.AppendLine("For write operations, explain your intent before calling file_write or file_edit.");
        builder.AppendLine("Use execute_command when a shell command is needed to complete the task.");
        builder.AppendLine("When context grows large, history is auto-compressed; full transcripts are kept under the session transcripts folder. Call compress to manually compact and end the current turn.");

        return builder.ToString();
    }

    private void AppendHostEnvironment(StringBuilder builder)
    {
        builder.AppendLine("Host environment (current Windows user session):");
        builder.AppendLine($"- Platform: {(host.IsWindows ? "Windows" : "non-Windows")}");
        builder.AppendLine($"- OS: {host.OsDescription} ({host.OsVersion})");
        builder.AppendLine($"- User: {host.UserDomainName}\\{host.UserName}");
        builder.AppendLine($"- Machine: {host.MachineName}");
        builder.AppendLine($"- User profile: {host.UserProfilePath}");
        builder.AppendLine($"- Process current directory: {host.CurrentDirectory}");
        builder.AppendLine($"- System directory: {host.SystemDirectory}");
        builder.AppendLine($"- Architecture: process={host.ProcessArchitecture}, OS={host.OsArchitecture}, processors={host.ProcessorCount}");
        builder.AppendLine($"- Agent app data: {host.AppDataDirectory}");
        builder.AppendLine($"- Default skills directory: {host.SkillsDirectory}");
    }

    private WorkspaceSettings? ResolveWorkspace(AgentSession session)
    {
        if (!string.IsNullOrWhiteSpace(session.ActiveWorkspace))
        {
            var rootPath = Path.GetFullPath(session.ActiveWorkspace);
            return new WorkspaceSettings
            {
                Name = Path.GetFileName(rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                RootPath = rootPath,
                IsDefault = true
            };
        }

        return settings.Workspaces.FirstOrDefault(workspace => !string.IsNullOrWhiteSpace(workspace.RootPath));
    }

}

public sealed class AgentRuntime(
    IAgentModelClient modelClient,
    IFileStorageService storage,
    IToolRouter toolRouter,
    IAgentEnvironmentPromptBuilder promptBuilder,
    IPreCompletionPipeline preCompletionPipeline,
    IAutoCompactService autoCompactService,
    IActiveAgentSessionContext activeSessionContext,
    IAppLogger logger) : IAgentRuntime
{
    private readonly IAppLogger _logger = logger.ForContext("AgentRuntime");

    public async Task<AgentSession> SendAsync(
        AgentSession session,
        string userInput,
        AgentTurnCallbacks? callbacks = null,
        CancellationToken cancellationToken = default)
    {
        activeSessionContext.SetSession(session.Id);
        try
        {
            return await SendAsyncCore(session, userInput, callbacks, cancellationToken);
        }
        finally
        {
            activeSessionContext.SetSession(null);
        }
    }

    private async Task<AgentSession> SendAsyncCore(
        AgentSession session,
        string userInput,
        AgentTurnCallbacks? callbacks,
        CancellationToken cancellationToken) =>
        SendAsyncTurnAsync(session, userInput, callbacks, cancellationToken);

    private async Task<AgentSession> SendAsyncTurnAsync(
        AgentSession session,
        string userInput,
        AgentTurnCallbacks? callbacks,
        CancellationToken cancellationToken)
    {
        try
        {
            var userMessage = ChatMessage.Create(MessageRole.User, userInput, session.Messages.LastOrDefault()?.Id);
            session = session.WithMessage(userMessage);
            await PersistMessageAsync(session, userMessage, cancellationToken);

            var tools = toolRouter.ListTools();
            var environmentPrompt = promptBuilder.Build(session, tools);
            var modelMessages = BuildModelMessages(environmentPrompt, session.Messages);

            if (ShouldListWorkspaceFiles(userInput) && tools.Any(tool => string.Equals(tool.Name, "file_list", StringComparison.OrdinalIgnoreCase)))
            {
                var toolCall = new AgentToolCall(Guid.NewGuid().ToString("N"), "file_list", new Dictionary<string, string>());
                await NotifyToolStartedAsync(callbacks, toolCall);
                session = await InvokeToolAndPersistAsync(session, userMessage.Id, toolCall, callbacks, cancellationToken);
                var toolContent = session.Messages[^1].Content;
                modelMessages.Add(new AgentModelMessage("system", $"Preflight tool result for the user's file listing request:{Environment.NewLine}{toolContent}"));
            }

            while (true)
            {
                session = await preCompletionPipeline.RunAsync(session, cancellationToken);
                modelMessages = BuildModelMessages(environmentPrompt, session.Messages);

                var response = await modelClient.CompleteAsync(new AgentModelRequest(modelMessages, tools), cancellationToken);
                if (response.ToolCalls.Count == 0)
                {
                    if (callbacks?.OnAssistantTextDelta is not null && !string.IsNullOrEmpty(response.Content))
                    {
                        await callbacks.OnAssistantTextDelta(response.Content);
                    }

                    var assistant = ChatMessage.Create(MessageRole.Assistant, response.Content, userMessage.Id);
                    session = session.WithMessage(assistant);
                    await NotifyMessageAsync(callbacks, assistant);
                    await PersistMessageAsync(session, assistant, cancellationToken);
                    _logger.Information("Saved session {SessionId} with {MessageCount} messages", session.Id, session.Messages.Count);
                    return session;
                }

                modelMessages.Add(new AgentModelMessage("assistant", response.Content, ToolCalls: response.ToolCalls));
                foreach (var toolCall in response.ToolCalls)
                {
                    await NotifyToolStartedAsync(callbacks, toolCall);

                    if (string.Equals(toolCall.Name, CompressTool.ToolName, StringComparison.OrdinalIgnoreCase))
                    {
                        session = await autoCompactService.CompactAsync(session, cancellationToken);
                        var compressedNote = ChatMessage.Create(
                            MessageRole.Assistant,
                            "Context compressed. Continue from the summary in the latest user message.",
                            userMessage.Id);
                        session = session.WithMessage(compressedNote);
                        await NotifyMessageAsync(callbacks, compressedNote);
                        await PersistMessageAsync(session, compressedNote, cancellationToken);
                        return session;
                    }

                    session = await InvokeToolAndPersistAsync(session, userMessage.Id, toolCall, callbacks, cancellationToken);
                    modelMessages.Add(new AgentModelMessage("tool", session.Messages[^1].Content, toolCall.Id));
                }
            }
        }
        catch (OperationCanceledException)
        {
            await storage.SaveSessionAsync(session, CancellationToken.None);
            throw;
        }
    }

    private async Task<AgentSession> InvokeToolAndPersistAsync(
        AgentSession session,
        string? parentMessageId,
        AgentToolCall toolCall,
        AgentTurnCallbacks? callbacks,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var result = await toolRouter.InvokeAsync(new ToolInvocation(toolCall.Name, toolCall.Arguments), cancellationToken);
        sw.Stop();

        await storage.AppendToolCallLogAsync(
            session.Id,
            new SessionToolCallLogEntry(
                DateTimeOffset.UtcNow,
                toolCall.Id,
                toolCall.Name,
                toolCall.Arguments,
                result.Succeeded,
                result.Summary,
                result.Content,
                result.Error,
                sw.ElapsedMilliseconds),
            cancellationToken);

        var content = FormatToolResult(toolCall, result);
        var toolMessage = ChatMessage.Create(MessageRole.Tool, content, parentMessageId);
        session = session.WithMessage(toolMessage);
        await NotifyMessageAsync(callbacks, toolMessage);
        await PersistMessageAsync(session, toolMessage, cancellationToken);
        return session;
    }

    private async Task PersistMessageAsync(AgentSession session, ChatMessage message, CancellationToken cancellationToken)
    {
        await storage.AppendConversationMessageAsync(session.Id, message, cancellationToken);
        await storage.SaveSessionAsync(session, cancellationToken);
    }

    private static async Task NotifyMessageAsync(AgentTurnCallbacks? callbacks, ChatMessage message)
    {
        if (callbacks?.OnMessage is not null)
        {
            await callbacks.OnMessage(message);
        }
    }

    private static async Task NotifyToolStartedAsync(AgentTurnCallbacks? callbacks, AgentToolCall toolCall)
    {
        if (callbacks?.OnToolStarted is not null)
        {
            await callbacks.OnToolStarted(toolCall);
        }
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

    public static string FormatToolResult(AgentToolCall call, ToolResult result)
    {
        var status = result.Succeeded ? "succeeded" : "failed";
        return string.Join(Environment.NewLine, new[]
        {
            $"ToolCallId: {call.Id}",
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
    public Task<AgentSession> SendAsync(
        AgentSession session,
        string userInput,
        AgentTurnCallbacks? callbacks = null,
        CancellationToken cancellationToken = default) =>
        agentRuntime.SendAsync(session, userInput, callbacks, cancellationToken);
}

public static class CompressTool
{
    public const string ToolName = "compress";
}
