using System.Diagnostics;
using System.Text;
using System.Text.Json.Serialization;
using Athlon.Agent.Core.Compaction;

namespace Athlon.Agent.Core;

public sealed class AppSettings
{
    public ModelSettings Model { get; set; } = new();
    public ToolPermissionSettings ToolPermissions { get; set; } = new();
    public List<McpServerSettings> McpServers { get; set; } = new();
    public List<SkillSettings> Skills { get; set; } = new();
    public List<WorkspaceSettings> Workspaces { get; set; } = new();
    public UiSettings Ui { get; set; } = new();
    public LoggingSettings Logging { get; set; } = new();
    public ContextCompactionSettings ContextCompaction { get; set; } = new();
    public PromptSettings Prompt { get; set; } = new();
    public PlanSettings Plan { get; set; } = new();
    public AgentTurnSettings AgentTurn { get; set; } = new();
    public WorkspaceIgnoreSettings WorkspaceIgnore { get; set; } = new();
    public FileReadSettings FileRead { get; set; } = new();
}

public sealed class FileReadSettings
{
    public long MaxFileBytes { get; set; } = 2 * 1024 * 1024;
    public int DefaultLineLimit { get; set; } = 500;
    public int MaxLinesPerCall { get; set; } = 2_000;
    public int MaxResponseChars { get; set; } = 32_768;
    public int MaxLineChars { get; set; } = 1_024;
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

public sealed class PlanSettings
{
    public int MaxSubtasks { get; set; } = 20;
    public string PlanFileName { get; set; } = "plan.md";
}

public sealed class PromptSettings
{
    public int MaxAgentsMdChars { get; set; } = 4000;
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
    public string Theme { get; set; } = "Dark";
    public double FontSize { get; set; } = 14;
    public bool ContextSidebarVisible { get; set; } = true;
    public double ContextSidebarWidth { get; set; } = 300;
    public double NavigationSidebarWidth { get; set; } = 220;
    public double EditorPaneWidth { get; set; } = 480;
    public double ComposerHeight { get; set; } = 168;
}
