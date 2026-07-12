using System.Diagnostics;
using System.Text;
using System.Text.Json.Serialization;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.Knowledge;
using Athlon.Agent.Core.Memory;
using Athlon.Agent.Core.SubAgents;
using Athlon.Agent.Core.Sso;
using Athlon.Agent.Core.TrainingData;

namespace Athlon.Agent.Core;

public sealed class ScheduleSettings
{
    public bool Enabled { get; set; } = false;
    public string DefaultWorkspaceRoot { get; set; } = "";
    public string Model { get; set; } = "auto";
    public string Mode { get; set; } = "agent";
    public string PromptPrefix { get; set; } = "";
    public bool KeepAwake { get; set; } = false;
    public List<ScheduledTask> Tasks { get; set; } = new();
}

public sealed class ScheduledTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Title { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string Prompt { get; set; } = "";
    public string WorkspaceRoot { get; set; } = "";
    public string Model { get; set; } = "auto";
    public string Mode { get; set; } = "agent";
    public string Kind { get; set; } = "daily";
    public int EveryMinutes { get; set; } = 60;
    public string TimeOfDay { get; set; } = "09:00";
    public string AtTime { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
    public string NextRunAt { get; set; } = "";
    public string LastRunAt { get; set; } = "";
    public string LastRunEndedAt { get; set; } = "";
    public string LastStatus { get; set; } = "idle";
    public string LastMessage { get; set; } = "";
    public string LastThreadId { get; set; } = "";
}

public sealed class AppSettings
{
    public ModelSettings Model { get; set; } = new();
    public ToolPermissionSettings ToolPermissions { get; set; } = new();
    public List<McpServerSettings> McpServers { get; set; } = new();
    public McpSearchSettings McpSearch { get; set; } = new();
    public List<SkillSettings> Skills { get; set; } = new();
    public List<WorkspaceSettings> Workspaces { get; set; } = new();
    public KnowledgeSettings Knowledge { get; set; } = new();
    public UiSettings Ui { get; set; } = new();
    public LoggingSettings Logging { get; set; } = new();
    public ContextCompactionSettings ContextCompaction { get; set; } = new();
    public PromptSettings Prompt { get; set; } = new();
    public AgentTurnSettings AgentTurn { get; set; } = new();
    public WorkspaceIgnoreSettings WorkspaceIgnore { get; set; } = new();
    public FileReadSettings FileRead { get; set; } = new();
    public SubAgentSettings SubAgent { get; set; } = new();
    public ParallelToolExecutionSettings ParallelToolExecution { get; set; } = new();
    public MemorySettings Memory { get; set; } = new();
    public ScheduleSettings Schedule { get; set; } = new();
    public UpdateSettings Update { get; set; } = new();
    public TrainingDataSettings TrainingData { get; set; } = new();
    public SsoSettings Sso { get; set; } = new();
}

public sealed class UpdateSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Base URL of the intranet update feed (must serve releases.win.json).</summary>
    public string BaseUrl { get; set; } = "";
}

public sealed class FileReadSettings
{
    public long MaxFileBytes { get; set; } = 2 * 1024 * 1024;
    public int DefaultLineLimit { get; set; } = 500;
    public int MaxLinesPerCall { get; set; } = 2_000;
    public int MaxResponseChars { get; set; } = 32_768;
    public int MaxLineChars { get; set; } = 1_0240;
    public bool CountTotalLines { get; set; } = true;
}

public sealed class WorkspaceIgnoreSettings
{
    /// <summary>
    /// Directory names skipped by grep_files, glob_files, file_list, and the workspace tree.
    /// Per-workspace <see cref="WorkspaceSettings.IgnorePatterns"/> overrides this when non-empty.
    /// </summary>
    public List<string> DirectoryNames { get; set; } = WorkspaceIgnoreDefaults.CreateMutableDefaultList();
}

public sealed class PromptSettings
{
    public int MaxAgentsMdChars { get; set; } = 8000;
    public int MaxContributingMdChars { get; set; } = 4000;
    public int MaxKnowledgeMdChars { get; set; } = 1500;
    public int MaxKnowledgeCatalogEntries { get; set; } = 50;
}
public sealed class ModelSettings
{
    public const string ApiKeySecretName = "model-api-key";

    public string Provider { get; set; } = "OpenAI-Compatible";
    public string Endpoint { get; set; } = "https://api.openai.com/v1";
    public string ModelName { get; set; } = "gpt-4.1-mini";

    /// <summary>
    /// Chat completion <c>max_tokens</c>. When null or 0, the field is omitted and the API default applies.
    /// Context-summary calls use <see cref="ContextCompactionSettings.SummaryMaxTokens"/> instead.
    /// </summary>
    public int? MaxTokens { get; set; }

    public bool EnableStreaming { get; set; } = true;
    public int StreamingIdleTimeoutSeconds { get; set; } = 90;

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
    /// <summary>
    /// When enabled, tools marked as requiring approval must receive an explicit
    /// user decision before execution. Disabled by default.
    /// </summary>
    public bool ApprovalEnabled { get; set; } = false;

    public bool AskBeforeEveryCommand { get; set; } = true;
    public string FileScopePolicy { get; set; } = "AskOutsideWorkspace";
    public List<string> CommandAllowList { get; set; } = new() { "git", "dotnet", "python", "node", "npm" };
    public List<string> CommandDenyList { get; set; } = new() { "format", "del /s", "rmdir /s", "Remove-Item -Recurse" };
}
public sealed class McpServerSettings
{
    public string Name { get; set; } = "filesystem";
    public bool Enabled { get; set; } = true;
    /// <summary>Claude Desktop field: <c>type</c> (<c>stdio</c> or <c>http</c> / streamable HTTP).</summary>
    public string TransportType { get; set; } = "stdio";
    /// <summary>Streamable HTTP MCP endpoint URL (required when <see cref="TransportType"/> is HTTP).</summary>
    public string Url { get; set; } = string.Empty;
    public string Command { get; set; } = "npx";
    public List<string> Args { get; set; } = new();
    public Dictionary<string, string> Env { get; set; } = new();
    /// <summary>Optional HTTP headers (e.g. Authorization) for streamable HTTP transport.</summary>
    public Dictionary<string, string> Headers { get; set; } = new();

    /// <summary>Working directory for stdio MCP server process (Claude Desktop <c>cwd</c>).</summary>
    public string WorkingDirectory { get; set; } = string.Empty;

    /// <summary>Application-level timeout for a single MCP tool call.</summary>
    public int ToolCallTimeoutSeconds { get; set; } = 120;
}

public sealed class McpSearchSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>direct | search | auto</summary>
    public string Mode { get; set; } = "auto";

    public int AutoThresholdToolCount { get; set; } = 12;

    public int AutoThresholdSchemaChars { get; set; } = 80_000;

    public int TopKDefault { get; set; } = 8;

    public int TopKMax { get; set; } = 20;

    public double MinScore { get; set; } = 0.5;
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
    /// <summary>When empty, inherits <see cref="AppSettings.WorkspaceIgnore"/>.</summary>
    public List<string> IgnorePatterns { get; set; } = new();
}
public sealed class UiSettings
{
    /// <summary>zh-CN | en-US</summary>
    public string Language { get; set; } = "zh-CN";
    public string Theme { get; set; } = "Dark";
    public double FontSize { get; set; } = 14;
    public bool ContextSidebarVisible { get; set; } = true;
    public double ContextSidebarWidth { get; set; } = 300;
    public double NavigationSidebarWidth { get; set; } = 220;
    public double EditorPaneWidth { get; set; } = 480;
    public double ComposerHeight { get; set; } = 168;
    /// <summary>When false (default), tool-call cards are hidden in chat UI.</summary>
    public bool ShowToolCalls { get; set; } = false;
}
