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

public static class ServiceCollectionExtensions
{
    [SupportedOSPlatform("windows")]
    public static IServiceCollection AddAthlonInfrastructure(this IServiceCollection services)
    {
        var paths = new AppPathProvider();
        paths.EnsureCreated();
        var jsonFileStore = new JsonFileStore();
        var settingsPath = Path.Combine(paths.ConfigPath, "settings.json");
        var settings = LoadSettings(settingsPath) ?? new AppSettings();
        var logger = AppLogger.Create(settings.Logging, paths.LogsPath);

        services.AddSingleton(settings);
        services.AddSingleton<IAppPathProvider>(paths);
        services.AddSingleton<IAgentHostEnvironment, AgentHostEnvironment>();
        services.AddAthlonSkills();
        services.AddSingleton<IJsonFileStore>(jsonFileStore);
        services.AddSingleton<ICredentialStore, DpapiCredentialStore>();
        services.AddSingleton<IAppLogger>(logger);
        services.AddSingleton<IFileStorageService, FileStorageService>();
        services.AddHttpClient<IAgentModelClient, OpenAiCompatibleChatModelClient>();
        services.AddSingleton<IAgentOrchestrator, AgentOrchestrator>();
        services.AddSingleton<IAgentRuntime, AgentRuntime>();
        services.AddSingleton<IAgentEnvironmentPromptBuilder, AgentEnvironmentPromptBuilder>();
        services.AddSingleton<IToolRouter, ToolRouter>();
        services.AddSingleton<IActiveWorkspaceContext, ActiveWorkspaceContext>();
        services.AddSingleton<IActiveAgentSessionContext, ActiveAgentSessionContext>();
        services.AddSingleton<ISessionHttpLogService, SessionHttpLogService>();
        services.AddSingleton<WorkspaceGuard>();
        services.AddSingleton<AuditLogService>();
        services.AddSingleton<IAgentTool, FileListTool>();
        services.AddSingleton<IAgentTool, FileReadTool>();
        services.AddSingleton<IAgentTool, FileWriteTool>();
        services.AddSingleton<IAgentTool, FileEditTool>();
        services.AddSingleton<IAgentTool, GrepFilesTool>();
        services.AddSingleton<IAgentTool, GlobFilesTool>();
        services.AddSingleton<IAgentTool, ExecuteCommandTool>();
        services.AddSingleton<MicrocompactService>();
        services.AddSingleton<IAutoCompactService, AutoCompactService>();
        services.AddSingleton<IPreCompletionPipeline, PreCompletionPipeline>();
        services.AddSingleton<IAgentTool, CompressAgentTool>();
        return services;
    }

    private static AppSettings? LoadSettings(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<AppSettings>(json, JsonFileStore.Options);
    }
}

public sealed class AppLogger : IAppLogger, IDisposable
{
    private readonly ILogger _logger;
    private readonly Logger? _rootLogger;

    private AppLogger(ILogger logger, Logger? rootLogger = null)
    {
        _logger = logger;
        _rootLogger = rootLogger;
    }

    public static AppLogger Create(LoggingSettings settings, string defaultLogDirectory)
    {
        var logDirectory = string.IsNullOrWhiteSpace(settings.Directory)
            ? defaultLogDirectory
            : settings.Directory;
        Directory.CreateDirectory(logDirectory);

        var level = Enum.TryParse<LogEventLevel>(settings.MinimumLevel, true, out var parsed) ? parsed : LogEventLevel.Information;
        var logger = new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(logDirectory, "app-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: settings.RetainedDays,
                fileSizeLimitBytes: settings.MaxFileSizeBytes,
                rollOnFileSizeLimit: true,
                shared: true)
            .CreateLogger();

        return new AppLogger(logger, logger);
    }

    public void Debug(string messageTemplate, params object[] values) => _logger.Debug(SensitiveText.Redact(messageTemplate), values);
    public void Information(string messageTemplate, params object[] values) => _logger.Information(SensitiveText.Redact(messageTemplate), values);
    public void Warning(string messageTemplate, params object[] values) => _logger.Warning(SensitiveText.Redact(messageTemplate), values);
    public void Error(Exception exception, string messageTemplate, params object[] values) => _logger.Error(exception, SensitiveText.Redact(messageTemplate), values);
    public IAppLogger ForContext(string sourceContext) => new AppLogger(_logger.ForContext("SourceContext", sourceContext));
    public void Dispose() => _rootLogger?.Dispose();
}

public sealed class FileStorageService(IAppLogger logger, IAppPathProvider paths, IJsonFileStore jsonFileStore) : IFileStorageService
{
    private readonly IAppLogger _logger = logger.ForContext("Storage");

    public string RootPath => paths.RootPath;

    public async Task SaveSessionAsync(AgentSession session, CancellationToken cancellationToken = default)
    {
        EnsureSessionLogDirectories(session.Id);
        var sessionDir = GetSessionDirectory(session);

        await jsonFileStore.SaveAsync(Path.Combine(sessionDir, "session.json"), session, cancellationToken);
        await AtomicFile.WriteAllTextAsync(Path.Combine(sessionDir, "conversation.md"), SessionMarkdownWriter.WriteConversation(session), cancellationToken);
        await RefreshIndexAsync(cancellationToken);
        _logger.Information("Session persisted to {SessionDir}", sessionDir);
    }

    public async Task SaveContextSummaryAsync(ContextSummary summary, CancellationToken cancellationToken = default)
    {
        var summaryDir = Path.Combine(paths.SessionsPath, summary.SessionId, "summaries");
        Directory.CreateDirectory(summaryDir);
        await AtomicFile.WriteAllTextAsync(Path.Combine(summaryDir, $"{summary.Id}.md"), SessionMarkdownWriter.WriteSummary(summary), cancellationToken);
    }

    public async Task<string> SaveTranscriptAsync(string sessionId, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        var transcriptDir = GetSessionTranscriptsDirectory(sessionId);
        Directory.CreateDirectory(transcriptDir);
        var path = Path.Combine(transcriptDir, $"transcript_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.jsonl");

        var builder = new StringBuilder();
        foreach (var message in messages)
        {
            builder.AppendLine(JsonSerializer.Serialize(message, JsonFileStore.JsonLineOptions));
        }

        await AtomicFile.WriteAllTextAsync(path, builder.ToString(), cancellationToken);
        return path;
    }

    public async Task AppendConversationMessageAsync(string sessionId, ChatMessage message, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        EnsureSessionLogDirectories(sessionId);
        var path = Path.Combine(GetSessionDirectory(sessionId), "conversation.jsonl");
        await jsonFileStore.AppendJsonLineAsync(
            path,
            new
            {
                time = message.CreatedAt,
                id = message.Id,
                role = message.Role.ToString(),
                parentId = message.ParentId,
                content = message.Content
            },
            cancellationToken);
    }

    public async Task AppendToolCallLogAsync(string sessionId, SessionToolCallLogEntry entry, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        EnsureSessionLogDirectories(sessionId);
        var path = Path.Combine(GetSessionDirectory(sessionId), "tool-calls", "calls.jsonl");
        await jsonFileStore.AppendJsonLineAsync(
            path,
            new
            {
                time = entry.Timestamp,
                toolCallId = entry.ToolCallId,
                toolName = entry.ToolName,
                arguments = entry.Arguments,
                succeeded = entry.Succeeded,
                summary = entry.Summary,
                content = HttpLogSanitizer.Truncate(entry.Content),
                error = entry.Error,
                durationMs = entry.DurationMs
            },
            cancellationToken);
    }

    public async Task<AgentSession?> LoadSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        var directPath = Path.Combine(GetSessionDirectory(sessionId), "session.json");
        if (File.Exists(directPath))
        {
            return await jsonFileStore.LoadAsync<AgentSession>(directPath, cancellationToken);
        }

        if (!Directory.Exists(paths.SessionsPath))
        {
            return null;
        }

        foreach (var file in Directory.EnumerateFiles(paths.SessionsPath, "session.json", SearchOption.AllDirectories))
        {
            var session = await jsonFileStore.LoadAsync<AgentSession>(file, cancellationToken);
            if (session is not null && string.Equals(session.Id, sessionId, StringComparison.Ordinal))
            {
                return session;
            }
        }

        return null;
    }

    public async Task<IReadOnlyList<SessionIndexEntry>> ListSessionsAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(paths.SessionsPath))
        {
            return Array.Empty<SessionIndexEntry>();
        }

        var result = new Dictionary<string, SessionIndexEntry>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(paths.SessionsPath, "session.json", SearchOption.AllDirectories))
        {
            var session = await jsonFileStore.LoadAsync<AgentSession>(file, cancellationToken);
            if (session is null)
            {
                continue;
            }

            if (!result.TryGetValue(session.Id, out var existing) || session.UpdatedAt > existing.UpdatedAt)
            {
                result[session.Id] = new SessionIndexEntry(session.Id, session.Title, Path.GetDirectoryName(file)!, session.UpdatedAt);
            }
        }

        return result.Values.OrderByDescending(item => item.UpdatedAt).ToArray();
    }

    public async Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        var deleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in await ListSessionsAsync(cancellationToken))
        {
            if (!string.Equals(entry.Id, sessionId, StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(entry.Path) && Directory.Exists(entry.Path))
            {
                Directory.Delete(entry.Path, true);
                deleted.Add(entry.Path);
            }
        }

        var directDir = GetSessionDirectory(sessionId);
        if (Directory.Exists(directDir) && !deleted.Contains(directDir))
        {
            Directory.Delete(directDir, true);
        }

        await RefreshIndexAsync(cancellationToken);
        _logger.Information("Deleted session {SessionId}", sessionId);
    }

    public async Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        settings.Model.LegacyApiKeyCredentialName = null;
        Directory.CreateDirectory(paths.ConfigPath);
        await jsonFileStore.SaveAsync(Path.Combine(paths.ConfigPath, "settings.json"), settings, cancellationToken);
        await jsonFileStore.SaveAsync(Path.Combine(paths.ConfigPath, "models.json"), settings.Model, cancellationToken);
        await jsonFileStore.SaveAsync(Path.Combine(paths.ConfigPath, "mcp.json"), settings.McpServers, cancellationToken);
        await jsonFileStore.SaveAsync(Path.Combine(paths.ConfigPath, "skills.json"), settings.Skills, cancellationToken);
        await jsonFileStore.SaveAsync(Path.Combine(paths.ConfigPath, "workspaces.json"), settings.Workspaces, cancellationToken);
        await jsonFileStore.SaveAsync(Path.Combine(paths.ConfigPath, "logging.json"), settings.Logging, cancellationToken);
    }

    public async Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default)
    {
        paths.EnsureCreated();
        var path = Path.Combine(paths.ConfigPath, "settings.json");
        var settings = await jsonFileStore.LoadAsync<AppSettings>(path, cancellationToken);
        if (settings is null)
        {
            var defaults = CreateDefaultSettings();
            await SaveSettingsAsync(defaults, cancellationToken);
            return defaults;
        }

        if (RemoveLegacyMyDocumentsWorkspace(settings))
        {
            await SaveSettingsAsync(settings, cancellationToken);
        }

        return settings;
    }

    private static AppSettings CreateDefaultSettings() => new();

    private static bool RemoveLegacyMyDocumentsWorkspace(AppSettings settings)
    {
        var myDocuments = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var removed = settings.Workspaces.RemoveAll(workspace =>
            !string.IsNullOrWhiteSpace(workspace.RootPath)
            &&
            string.Equals(Path.GetFullPath(workspace.RootPath), Path.GetFullPath(myDocuments), StringComparison.OrdinalIgnoreCase));

        return removed > 0;
    }

    private async Task RefreshIndexAsync(CancellationToken cancellationToken)
    {
        var index = await ListSessionsAsync(cancellationToken);
        Directory.CreateDirectory(paths.SessionsPath);
        await jsonFileStore.SaveAsync(Path.Combine(paths.SessionsPath, "index.json"), index, cancellationToken);
    }

    private void EnsureSessionLogDirectories(string sessionId)
    {
        var sessionDir = GetSessionDirectory(sessionId);
        Directory.CreateDirectory(sessionDir);
        Directory.CreateDirectory(Path.Combine(sessionDir, "tool-calls"));
        Directory.CreateDirectory(Path.Combine(sessionDir, "summaries"));
        Directory.CreateDirectory(Path.Combine(sessionDir, "transcripts"));
        Directory.CreateDirectory(Path.Combine(sessionDir, "http"));
    }

    private string GetSessionDirectory(AgentSession session) => GetSessionDirectory(session.Id);

    private string GetSessionDirectory(string sessionId) => Path.Combine(paths.SessionsPath, sessionId);

    private string GetSessionTranscriptsDirectory(string sessionId) =>
        Path.Combine(GetSessionDirectory(sessionId), "transcripts");
}

[SupportedOSPlatform("windows")]
public sealed class DpapiCredentialStore(IAppPathProvider paths) : ICredentialStore
{
    public Task SaveSecretAsync(string name, string secret, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(paths.CredentialsPath);
        var plainBytes = Encoding.UTF8.GetBytes(secret);
        var protectedBytes = ProtectedData.Protect(plainBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        File.WriteAllText(GetCredentialPath(name), Convert.ToBase64String(protectedBytes));
        return Task.CompletedTask;
    }

    public Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken = default)
    {
        var path = GetCredentialPath(name);
        if (!File.Exists(path))
        {
            return Task.FromResult<string?>(null);
        }

        var encoded = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(encoded))
        {
            return Task.FromResult<string?>(null);
        }

        var protectedBytes = Convert.FromBase64String(encoded);
        var plainBytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        return Task.FromResult<string?>(Encoding.UTF8.GetString(plainBytes));
    }

    public Task<bool> HasSecretAsync(string name, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(File.Exists(GetCredentialPath(name)));
    }

    private string GetCredentialPath(string name)
    {
        var safeName = FileNameSanitizer.Sanitize(string.IsNullOrWhiteSpace(name) ? "default" : name);
        return Path.Combine(paths.CredentialsPath, $"{safeName}.secret");
    }
}

public sealed class OpenAiCompatibleChatModelClient(
    HttpClient httpClient,
    IAppLogger logger,
    AppSettings settings,
    ICredentialStore credentialStore,
    ISessionHttpLogService sessionHttpLog,
    IActiveAgentSessionContext activeSessionContext) : IAgentModelClient
{
    private readonly IAppLogger _logger = logger.ForContext("ModelGateway");

    public async Task<AgentModelResponse> CompleteAsync(
        AgentModelRequest request,
        Func<string, Task>? onTextDelta = null,
        CancellationToken cancellationToken = default)
    {
        var apiKey = await credentialStore.GetSecretAsync(ModelSettings.ApiKeySecretName, cancellationToken);
        if (string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(settings.Model.LegacyApiKeyCredentialName))
        {
            apiKey = await credentialStore.GetSecretAsync(settings.Model.LegacyApiKeyCredentialName, cancellationToken);
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                await credentialStore.SaveSecretAsync(ModelSettings.ApiKeySecretName, apiKey, cancellationToken);
            }
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("模型 API Key 未配置。请在 Settings > Model 中输入 API Key 并保存，或设置环境变量 OPENAI_API_KEY。");
        }

        var preferStreaming = settings.Model.EnableStreaming
            && onTextDelta is not null
            && request.AllowToolCalls;

        if (preferStreaming)
        {
            try
            {
                return await CompleteOpenAiCompatibleAsync(request, apiKey, stream: true, onTextDelta, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Warning("Streaming completion failed, fallback to non-stream mode: {Message}", ex.Message);
            }
        }

        return await CompleteOpenAiCompatibleAsync(request, apiKey, stream: false, onTextDelta, cancellationToken);
    }

    private async Task<AgentModelResponse> CompleteOpenAiCompatibleAsync(
        AgentModelRequest request,
        string apiKey,
        bool stream,
        Func<string, Task>? onTextDelta,
        CancellationToken cancellationToken)
    {
        var endpoint = settings.Model.Endpoint.TrimEnd('/') + "/chat/completions";
        var purpose = !request.AllowToolCalls && request.MaxTokens.HasValue ? "context-summary" : "chat-completion";
        var payload = new Dictionary<string, object?>
        {
            ["model"] = settings.Model.ModelName,
            ["stream"] = stream,
            ["messages"] = request.Messages.Select(ToOpenAiMessage).ToArray()
        };

        if (request.AllowToolCalls && request.Tools.Count > 0)
        {
            payload["tools"] = request.Tools.Select(ToOpenAiTool).ToArray();
            payload["tool_choice"] = "auto";
        }

        if (request.MaxTokens is > 0)
        {
            payload["max_tokens"] = request.MaxTokens.Value;
        }

        var sessionId = activeSessionContext.SessionId;
        var sw = Stopwatch.StartNew();
        string? responseBody = null;
        int? statusCode = null;
        string? error = null;

        try
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(payload)
            };
            httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

            using var response = await httpClient.SendAsync(
                httpRequest,
                stream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead,
                cancellationToken);
            statusCode = (int)response.StatusCode;

            if (!response.IsSuccessStatusCode)
            {
                responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                error = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                _logger.Warning(
                    "Model HTTP failed {StatusCode} for session {SessionId}: {Body}",
                    statusCode,
                    sessionId,
                    HttpLogSanitizer.Truncate(responseBody));
                throw new HttpRequestException($"{error}. Body: {HttpLogSanitizer.Truncate(responseBody)}");
            }

            if (stream)
            {
                return await ParseStreamingResponseAsync(response, onTextDelta, body => responseBody = body, cancellationToken);
            }

            responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var nonStreamResult = ParseNonStreamingResponse(responseBody);
            if (onTextDelta is not null && !string.IsNullOrEmpty(nonStreamResult.Content))
            {
                await onTextDelta(nonStreamResult.Content);
            }

            return nonStreamResult;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error ??= ex.Message;
            throw;
        }
        finally
        {
            sw.Stop();
            await sessionHttpLog.LogInteractionAsync(
                sessionId,
                new SessionHttpInteractionLog(
                    DateTimeOffset.UtcNow,
                    endpoint,
                    purpose,
                    statusCode,
                    payload,
                    responseBody,
                    error,
                    sw.ElapsedMilliseconds),
                CancellationToken.None);
        }
    }

    private static AgentModelResponse ParseNonStreamingResponse(string responseBody)
    {
        using var json = JsonDocument.Parse(responseBody);
        var message = json.RootElement.GetProperty("choices")[0].GetProperty("message");
        var content = message.TryGetProperty("content", out var contentElement) && contentElement.ValueKind == JsonValueKind.String
            ? contentElement.GetString() ?? string.Empty
            : string.Empty;

        var toolCalls = ParseToolCallsFromMessage(message);
        return new AgentModelResponse(content, toolCalls);
    }

    private static List<AgentToolCall> ParseToolCallsFromMessage(JsonElement message)
    {
        var toolCalls = new List<AgentToolCall>();
        if (message.TryGetProperty("tool_calls", out var callsElement) && callsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var call in callsElement.EnumerateArray())
            {
                var function = call.GetProperty("function");
                var id = call.GetProperty("id").GetString() ?? Guid.NewGuid().ToString("N");
                var name = function.GetProperty("name").GetString() ?? string.Empty;
                var argumentsJson = function.TryGetProperty("arguments", out var argumentsElement) && argumentsElement.ValueKind == JsonValueKind.String
                    ? argumentsElement.GetString() ?? "{}"
                    : "{}";
                toolCalls.Add(new AgentToolCall(id, name, ParseArguments(argumentsJson)));
            }
        }

        return toolCalls;
    }

    private static async Task<AgentModelResponse> ParseStreamingResponseAsync(
        HttpResponseMessage response,
        Func<string, Task>? onTextDelta,
        Action<string> setResponseBody,
        CancellationToken cancellationToken)
    {
        var contentBuilder = new StringBuilder();
        var rawBuilder = new StringBuilder();
        var toolCalls = new Dictionary<int, StreamingToolCallState>();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null || !line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var data = line["data:".Length..].Trim();
            if (string.IsNullOrWhiteSpace(data))
            {
                continue;
            }

            rawBuilder.AppendLine(data);
            if (string.Equals(data, "[DONE]", StringComparison.Ordinal))
            {
                break;
            }

            using var chunk = JsonDocument.Parse(data);
            if (!chunk.RootElement.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var choice in choices.EnumerateArray())
            {
                if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (delta.TryGetProperty("content", out var token) && token.ValueKind == JsonValueKind.String)
                {
                    var tokenText = token.GetString() ?? string.Empty;
                    if (!string.IsNullOrEmpty(tokenText))
                    {
                        contentBuilder.Append(tokenText);
                        if (onTextDelta is not null)
                        {
                            await onTextDelta(tokenText);
                        }
                    }
                }

                if (delta.TryGetProperty("tool_calls", out var deltaToolCalls) && deltaToolCalls.ValueKind == JsonValueKind.Array)
                {
                    foreach (var partial in deltaToolCalls.EnumerateArray())
                    {
                        var index = partial.TryGetProperty("index", out var indexElement) && indexElement.TryGetInt32(out var parsedIndex)
                            ? parsedIndex
                            : toolCalls.Count;
                        if (!toolCalls.TryGetValue(index, out var state))
                        {
                            state = new StreamingToolCallState();
                            toolCalls[index] = state;
                        }

                        if (partial.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String)
                        {
                            state.Id = idElement.GetString() ?? state.Id;
                        }

                        if (partial.TryGetProperty("function", out var function) && function.ValueKind == JsonValueKind.Object)
                        {
                            if (function.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String)
                            {
                                state.Name = nameElement.GetString() ?? state.Name;
                            }

                            if (function.TryGetProperty("arguments", out var argsElement) && argsElement.ValueKind == JsonValueKind.String)
                            {
                                state.Arguments.Append(argsElement.GetString());
                            }
                        }
                    }
                }
            }
        }

        setResponseBody(rawBuilder.ToString());
        var normalizedToolCalls = toolCalls
            .OrderBy(item => item.Key)
            .Select(item =>
            {
                var state = item.Value;
                var args = state.Arguments.Length == 0 ? "{}" : state.Arguments.ToString();
                return new AgentToolCall(
                    string.IsNullOrWhiteSpace(state.Id) ? Guid.NewGuid().ToString("N") : state.Id,
                    state.Name ?? string.Empty,
                    ParseArguments(args));
            })
            .ToArray();

        return new AgentModelResponse(contentBuilder.ToString(), normalizedToolCalls);
    }

    private sealed class StreamingToolCallState
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public StringBuilder Arguments { get; } = new();
    }

    private static Dictionary<string, object?> ToOpenAiMessage(AgentModelMessage message)
    {
        var result = new Dictionary<string, object?>
        {
            ["role"] = message.Role,
            ["content"] = message.Content
        };

        if (!string.IsNullOrWhiteSpace(message.ToolCallId))
        {
            result["tool_call_id"] = message.ToolCallId;
        }

        if (message.ToolCalls is { Count: > 0 })
        {
            result["tool_calls"] = message.ToolCalls.Select(call => new
            {
                id = call.Id,
                type = "function",
                function = new
                {
                    name = call.Name,
                    arguments = JsonSerializer.Serialize(call.Arguments)
                }
            }).ToArray();
        }

        return result;
    }

    private static object ToOpenAiTool(ToolDefinition tool)
    {
        var properties = tool.Parameters.ToDictionary(
            parameter => parameter.Key,
            parameter => (object)new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = parameter.Value
            });

        return new
        {
            type = "function",
            function = new
            {
                name = tool.Name,
                description = tool.Description,
                parameters = new
                {
                    type = "object",
                    properties,
                    required = tool.Parameters.Where(parameter => !parameter.Value.StartsWith("Optional", StringComparison.OrdinalIgnoreCase)).Select(parameter => parameter.Key).ToArray()
                }
            }
        };
    }

    private static IReadOnlyDictionary<string, string> ParseArguments(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return new Dictionary<string, string>();
        }

        using var json = JsonDocument.Parse(argumentsJson);
        if (json.RootElement.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, string>();
        }

        return json.RootElement.EnumerateObject().ToDictionary(
            property => property.Name,
            property => property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? string.Empty : property.Value.GetRawText());
    }
}

public sealed class AuditLogService(IAppLogger logger, IAppPathProvider paths, IJsonFileStore jsonFileStore)
{
    private readonly IAppLogger _logger = logger.ForContext("Audit");

    public async Task WriteAsync(string action, object payload, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(paths.AuditPath, $"audit-{DateTimeOffset.Now:yyyy-MM-dd}.jsonl");
        await jsonFileStore.AppendJsonLineAsync(path, new { time = DateTimeOffset.UtcNow, action, payload }, cancellationToken);
        _logger.Information("Audit entry written for {Action}", action);
    }
}

public sealed class ActiveWorkspaceContext : IActiveWorkspaceContext
{
    private static readonly string[] DefaultIgnorePatterns = [".git", "bin", "obj", "node_modules", ".vs", "artifacts", "publish"];

    public string? RootPath { get; private set; }
    public string? DisplayName { get; private set; }
    public IReadOnlyList<string> IgnorePatterns { get; private set; } = DefaultIgnorePatterns;

    public void SetWorkspace(string? rootPath, string? displayName = null, IReadOnlyList<string>? ignorePatterns = null)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            RootPath = null;
            DisplayName = null;
            IgnorePatterns = DefaultIgnorePatterns;
            return;
        }

        RootPath = Path.GetFullPath(rootPath);
        DisplayName = displayName ?? Path.GetFileName(RootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        IgnorePatterns = ignorePatterns ?? DefaultIgnorePatterns;
    }
}

public sealed class WorkspaceGuard(IActiveWorkspaceContext workspaceContext, AppSettings settings, IAppPathProvider paths)
{
    public bool HasConfiguredWorkspace => TryGetWorkspaceRoot(out _);

    public bool IsInsideWorkspace(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(path);
        return GetAllowedRoots().Any(root => IsPathUnderRoot(fullPath, root));
    }

    public string Normalize(string path, string? cwd = null)
    {
        if (!TryGetWorkspaceRoot(out var basePath))
        {
            throw new InvalidOperationException("工作区尚未设定。请先在侧栏「配置」或设置页指定工作区目录。");
        }

        var rooted = Path.IsPathRooted(path) ? path : Path.Combine(cwd ?? basePath, path);
        return Path.GetFullPath(rooted);
    }

    public IReadOnlyList<string> GetIgnorePatterns() =>
        workspaceContext.IgnorePatterns.Count > 0
            ? workspaceContext.IgnorePatterns
            : settings.Workspaces.FirstOrDefault(workspace => !string.IsNullOrWhiteSpace(workspace.RootPath))?.IgnorePatterns
              ?? [".git", "bin", "obj", "node_modules"];

    private bool TryGetWorkspaceRoot(out string rootPath)
    {
        if (!string.IsNullOrWhiteSpace(workspaceContext.RootPath))
        {
            rootPath = workspaceContext.RootPath;
            return true;
        }

        var configured = settings.Workspaces.FirstOrDefault(workspace => !string.IsNullOrWhiteSpace(workspace.RootPath));
        if (configured is null)
        {
            rootPath = string.Empty;
            return false;
        }

        rootPath = configured.RootPath;
        return true;
    }

    private IEnumerable<string> GetAllowedRoots()
    {
        if (TryGetWorkspaceRoot(out var workspaceRoot))
        {
            yield return workspaceRoot;
        }

        if (!string.IsNullOrWhiteSpace(paths.RootPath))
        {
            yield return paths.RootPath;
        }
    }

    private static bool IsPathUnderRoot(string fullPath, string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return false;
        }

        var normalizedRoot = Path.GetFullPath(rootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(fullPath);

        return normalizedPath.Equals(normalizedRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
               || normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class FileListTool(WorkspaceGuard guard, AuditLogService audit) : IAgentTool
{
    public ToolDefinition Definition { get; } = new(
        "file_list",
        "List files in the active workspace or a workspace subdirectory.",
        new Dictionary<string, string> { ["path"] = "Optional workspace-relative directory path" });

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        var requestedPath = invocation.Arguments.GetValueOrDefault("path") ?? ".";
        var fullPath = guard.Normalize(requestedPath);
        if (!guard.IsInsideWorkspace(fullPath))
        {
            return ToolResult.Failure("Outside workspace", fullPath);
        }

        if (!Directory.Exists(fullPath))
        {
            return ToolResult.Failure("Directory not found", fullPath);
        }

        var ignorePatterns = guard.GetIgnorePatterns();
        var files = Directory.EnumerateFileSystemEntries(fullPath, "*", SearchOption.TopDirectoryOnly)
            .Where(path => !ignorePatterns.Any(pattern => string.Equals(Path.GetFileName(path), pattern, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(path => Directory.Exists(path) ? 0 : 1)
            .ThenBy(Path.GetFileName)
            .Take(200)
            .Select(path => Directory.Exists(path) ? $"[DIR]  {Path.GetFileName(path)}" : $"[FILE] {Path.GetFileName(path)} ({new FileInfo(path).Length} bytes)")
            .ToArray();

        await audit.WriteAsync("file_list", new { path = fullPath, count = files.Length }, cancellationToken);
        var content = files.Length == 0 ? "(empty directory)" : string.Join(Environment.NewLine, files);
        return ToolResult.Success($"Listed {files.Length} entries from {Path.GetFileName(fullPath)}", content);
    }
}

public sealed class FileReadTool(WorkspaceGuard guard, AuditLogService audit) : IAgentTool
{
    public ToolDefinition Definition { get; } = new(
        "file_read",
        "Read workspace file content with line numbers. Supports pagination via offset/limit or start_line/end_line.",
        new Dictionary<string, string>
        {
            ["path"] = "File path",
            ["offset"] = "Optional 0-indexed start line. Default: 0",
            ["limit"] = "Optional max lines to return. Default: all lines",
            ["start_line"] = "Optional 1-indexed start line",
            ["end_line"] = "Optional 1-indexed end line"
        });

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!ToolArguments.TryGetRequired(invocation, "path", out var path, out var error)) return error;
        var fullPath = guard.Normalize(path);
        if (!guard.IsInsideWorkspace(fullPath)) return ToolResult.Failure("Outside workspace", fullPath);
        if (!File.Exists(fullPath)) return ToolResult.Failure("File not found", fullPath);
        var lines = await File.ReadAllLinesAsync(fullPath, cancellationToken);
        var selected = SelectLines(lines, invocation).ToArray();
        await audit.WriteAsync("file_read", new { path = fullPath, lines = lines.Length }, cancellationToken);
        return ToolResult.Success($"Read {selected.Length} of {lines.Length} lines from {Path.GetFileName(fullPath)}", string.Join(Environment.NewLine, selected));
    }

    private static IEnumerable<string> SelectLines(string[] lines, ToolInvocation invocation)
    {
        var offset = ToolArguments.GetInt32(invocation, "offset", 0);
        var limit = ToolArguments.GetInt32(invocation, "limit", 0);
        var startLine = ToolArguments.GetInt32(invocation, "start_line", 0);
        var endLine = ToolArguments.GetInt32(invocation, "end_line", 0);

        if (startLine > 0)
        {
            offset = Math.Max(0, startLine - 1);
            limit = endLine >= startLine ? endLine - startLine + 1 : 0;
        }

        var selected = lines
            .Skip(Math.Max(0, offset))
            .Take(limit <= 0 ? 500 : Math.Min(limit, 500))
            .Select((line, index) => $"{offset + index + 1}|{line}");

        return selected;
    }
}

public sealed class FileWriteTool(WorkspaceGuard guard, AuditLogService audit) : IAgentTool
{
    public ToolDefinition Definition { get; } = new("file_write", "Create or overwrite a workspace file with backup.", new Dictionary<string, string> { ["path"] = "File path", ["content"] = "New content" }, RequiresApproval: true);

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!ToolArguments.TryGetRequired(invocation, "path", out var path, out var error)) return error;
        if (!ToolArguments.TryGetRequired(invocation, "content", out var content, out error)) return error;

        var fullPath = guard.Normalize(path);
        if (!guard.IsInsideWorkspace(fullPath)) return ToolResult.Failure("Outside workspace", fullPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        AtomicFile.BackupIfExists(fullPath);
        await File.WriteAllTextAsync(fullPath, content, cancellationToken);
        await audit.WriteAsync("file_write", new { path = fullPath, chars = content.Length }, cancellationToken);
        return ToolResult.Success($"Wrote {content.Length} chars to {Path.GetFileName(fullPath)}");
    }
}

public sealed class FileEditTool(WorkspaceGuard guard, AuditLogService audit) : IAgentTool
{
    public ToolDefinition Definition { get; } = new("file_edit", "Replace text in a workspace file with backup. old_text must be unique unless replace_all is true.", new Dictionary<string, string> { ["path"] = "File path", ["old_text"] = "Unique text", ["new_text"] = "Replacement", ["replace_all"] = "Optional true to replace all occurrences" }, RequiresApproval: true);

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!ToolArguments.TryGetRequired(invocation, "path", out var path, out var error)) return error;
        if (!ToolArguments.TryGetRequired(invocation, "old_text", out var oldText, out error)) return error;
        if (!ToolArguments.TryGetRequired(invocation, "new_text", out var newText, out error)) return error;

        var fullPath = guard.Normalize(path);
        if (!guard.IsInsideWorkspace(fullPath)) return ToolResult.Failure("Outside workspace", fullPath);
        var content = await File.ReadAllTextAsync(fullPath, cancellationToken);
        var replaceAll = invocation.Arguments.TryGetValue("replace_all", out var value) && bool.TryParse(value, out var parsed) && parsed;
        var occurrences = content.Split(oldText).Length - 1;
        if (occurrences == 0) return ToolResult.Failure("Text not found", "old_text did not match.");
        if (!replaceAll && occurrences != 1) return ToolResult.Failure("Text is not unique", "old_text must match exactly once unless replace_all is true.");
        AtomicFile.BackupIfExists(fullPath);
        var updated = replaceAll ? content.Replace(oldText, newText) : ReplaceFirst(content, oldText, newText);
        await File.WriteAllTextAsync(fullPath, updated, cancellationToken);
        await audit.WriteAsync("file_edit", new { path = fullPath, oldChars = oldText.Length, newChars = newText.Length, occurrences = replaceAll ? occurrences : 1 }, cancellationToken);
        return ToolResult.Success($"Edited {Path.GetFileName(fullPath)} ({(replaceAll ? occurrences : 1)} replacement(s))");
    }

    private static string ReplaceFirst(string content, string oldText, string newText)
    {
        var index = content.IndexOf(oldText, StringComparison.Ordinal);
        return index < 0 ? content : content[..index] + newText + content[(index + oldText.Length)..];
    }
}

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
            ["path"] = "Optional directory or file path to search",
            ["glob"] = "Optional file glob filter, e.g. *.cs"
        });

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!ToolArguments.TryGetRequired(invocation, "pattern", out var pattern, out var error)) return error;
        var requestedPath = invocation.Arguments.GetValueOrDefault("path") ?? ".";
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

public sealed class GlobFilesTool(WorkspaceGuard guard, AuditLogService audit) : IAgentTool
{
    public ToolDefinition Definition { get; } = new(
        "glob_files",
        "Find workspace files matching a glob pattern.",
        new Dictionary<string, string>
        {
            ["pattern"] = "Glob pattern, e.g. **/*.cs",
            ["path"] = "Optional base directory to search from"
        });

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!ToolArguments.TryGetRequired(invocation, "pattern", out var pattern, out var error)) return error;
        var requestedPath = invocation.Arguments.GetValueOrDefault("path") ?? ".";
        var fullPath = guard.Normalize(requestedPath);
        if (!guard.IsInsideWorkspace(fullPath)) return ToolResult.Failure("Outside workspace", fullPath);
        if (!Directory.Exists(fullPath)) return ToolResult.Failure("Directory not found", fullPath);

        var searchPattern = pattern.Contains('/') || pattern.Contains('\\') ? Path.GetFileName(pattern) : pattern;
        var recursive = pattern.Contains("**", StringComparison.Ordinal);
        var matches = Directory.EnumerateFileSystemEntries(fullPath, searchPattern.Replace("**", "*"), recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .Take(200)
            .Select(path => Directory.Exists(path)
                ? $"{Path.GetRelativePath(fullPath, path)}/"
                : $"{Path.GetRelativePath(fullPath, path)} ({new FileInfo(path).Length} bytes)")
            .ToArray();

        await audit.WriteAsync("glob_files", new { path = fullPath, pattern, count = matches.Length }, cancellationToken);
        return matches.Length == 0
            ? ToolResult.Success("No matching files found", "No matching files found")
            : ToolResult.Success($"Found {matches.Length} matching entries", string.Join(Environment.NewLine, matches));
    }
}

public sealed class ExecuteCommandTool(AppSettings settings, AuditLogService audit) : IAgentTool
{
    public ToolDefinition Definition { get; } = new("execute_command", "Execute a command after explicit user approval.", new Dictionary<string, string> { ["command"] = "Command line", ["cwd"] = "Working directory", ["timeout"] = "Timeout seconds" }, RequiresApproval: true);

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!ToolArguments.TryGetRequired(invocation, "command", out var command, out var error)) return error;
        if (settings.ToolPermissions.CommandDenyList.Any(deny => command.Contains(deny, StringComparison.OrdinalIgnoreCase))) return ToolResult.Failure("Command denied", command);

        var cwd = invocation.Arguments.GetValueOrDefault("cwd") ?? Environment.CurrentDirectory;
        var timeout = ToolArguments.GetInt32(invocation, "timeout", 60);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeout));

        var startInfo = new ProcessStartInfo("cmd.exe", "/c " + command) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        var sw = Stopwatch.StartNew();
        using var process = Process.Start(startInfo)!;
        var stdout = await process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderr = await process.StandardError.ReadToEndAsync(cts.Token);
        await process.WaitForExitAsync(cts.Token);
        sw.Stop();
        var content = string.IsNullOrWhiteSpace(stderr) ? stdout : stdout + Environment.NewLine + stderr;
        await audit.WriteAsync("execute_command", new { command, cwd, process.ExitCode, elapsedMs = sw.ElapsedMilliseconds }, cancellationToken);
        return process.ExitCode == 0 ? ToolResult.Success($"Command exited 0 in {sw.ElapsedMilliseconds}ms", content, sw.Elapsed) : ToolResult.Failure("Command failed", content, sw.Elapsed);
    }
}
