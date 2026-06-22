using System.Collections.ObjectModel;
using System.IO;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Mcp;
using Athlon.Agent.Skills;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Athlon.Agent.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly IMcpRegistry _mcpRegistry;
    private readonly IAgentSkillCatalog _skillCatalog;
    private readonly IAppPathProvider _paths;

    public SettingsViewModel(
        AppSettings settings,
        IMcpRegistry mcpRegistry,
        IAgentSkillCatalog skillCatalog,
        IAppPathProvider paths)
    {
        Settings = settings;
        _mcpRegistry = mcpRegistry;
        _skillCatalog = skillCatalog;
        _paths = paths;
        foreach (var server in Settings.McpServers)
        {
            McpServers.Add(new McpServerItemViewModel(server, _mcpRegistry, OnMcpServerEnabledChanged));
        }

        SelectedMcpServer = McpServers.FirstOrDefault();
        SyncSkillsFromCatalog();
    }

    public event EventHandler? McpConfigurationChanged;
    public event EventHandler? SkillConfigurationChanged;

    private void OnMcpServerEnabledChanged() => McpConfigurationChanged?.Invoke(this, EventArgs.Empty);

    private void OnSkillEnabledChanged() => SkillConfigurationChanged?.Invoke(this, EventArgs.Empty);

    internal void RefreshRuntimeStates()
    {
        foreach (var server in McpServers)
        {
            server.RefreshRuntimeState();
        }
    }

    public void SyncSkillsFromCatalog()
    {
        _skillCatalog.Reload();
        var installed = _skillCatalog.Skills;
        var installedNames = installed.Select(skill => skill.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var merged = SkillSettingsMerger.Merge(_paths.SkillsPath, installed, Settings.Skills);
        Settings.Skills.Clear();
        Settings.Skills.AddRange(merged);

        Skills.Clear();
        foreach (var settings in merged.OrderBy(skill => skill.Name, StringComparer.Ordinal))
        {
            var description = installed.FirstOrDefault(skill =>
                string.Equals(skill.Name, settings.Name, StringComparison.OrdinalIgnoreCase))?.Description
                ?? string.Empty;
            var isInstalled = installedNames.Contains(settings.Name);
            Skills.Add(new SkillItemViewModel(settings, description, isInstalled, OnSkillEnabledChanged));
        }
    }

    public AppSettings Settings { get; }
    public string SettingsConfigPath => Path.Combine(_paths.ConfigPath, "settings.json");
    public string SkillsDirectoryPath => _paths.SkillsPath;
    public string SkillsSettingsDescription =>
        $"技能从 {SkillsDirectoryPath} 自动加载；此页面控制每个技能是否启用。关闭后不会出现在系统提示与 @skill 补全中。保存设置后写入 {SettingsConfigPath}。";
    public string[] Sections { get; } = { "Models", "MCP", "Skills", "Workspace", "Tool Permissions", "Appearance" };
    public ObservableCollection<McpServerItemViewModel> McpServers { get; } = new();
    public ObservableCollection<SkillItemViewModel> Skills { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedMcpServer))]
    [NotifyPropertyChangedFor(nameof(EditableMcpArgs))]
    private McpServerItemViewModel? selectedMcpServer;

    public bool HasSelectedMcpServer => SelectedMcpServer is not null;

    public McpServerSettings EditableMcpServer
    {
        get
        {
            if (SelectedMcpServer is null)
            {
                AddMcpServer();
            }

            return SelectedMcpServer!.Settings;
        }
    }

    public string EditableMcpArgs
    {
        get => SelectedMcpServer?.ArgsText ?? string.Empty;
        set
        {
            if (SelectedMcpServer is not null)
            {
                SelectedMcpServer.ArgsText = value;
            }
        }
    }

    [RelayCommand]
    private void AddMcpServer()
    {
        var nextIndex = Settings.McpServers.Count + 1;
        var server = new McpServerSettings
        {
            Name = $"custom-mcp-{nextIndex}",
            Command = "npx",
            Enabled = true
        };
        server.Args.Add("-y");

        Settings.McpServers.Add(server);
        var item = new McpServerItemViewModel(server, _mcpRegistry, OnMcpServerEnabledChanged);
        McpServers.Add(item);
        SelectedMcpServer = item;
    }

    [RelayCommand]
    private void SelectMcpServer(McpServerItemViewModel server)
    {
        SelectedMcpServer = server;
    }

    [RelayCommand]
    private void DeleteMcpServer(McpServerItemViewModel? server)
    {
        if (server is null)
        {
            return;
        }

        Settings.McpServers.Remove(server.Settings);
        McpServers.Remove(server);
        if (ReferenceEquals(SelectedMcpServer, server))
        {
            SelectedMcpServer = McpServers.FirstOrDefault();
        }

        McpConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }

    public WorkspaceSettings EditableWorkspace
    {
        get
        {
            if (Settings.Workspaces.Count == 0)
            {
                Settings.Workspaces.Add(new WorkspaceSettings());
            }

            return Settings.Workspaces[0];
        }
    }

    internal static void PruneEmptyWorkspaces(AppSettings settings) =>
        settings.Workspaces.RemoveAll(workspace => string.IsNullOrWhiteSpace(workspace.RootPath));

    public string ModelMaxTokensText
    {
        get => Settings.Model.MaxTokens is > 0
            ? Settings.Model.MaxTokens.Value.ToString()
            : string.Empty;
        set => Settings.Model.MaxTokens = ParseOptionalPositiveInt(value);
    }

    public string KnowledgeEmbeddingDimensionText
    {
        get => Settings.Knowledge.Embedding.Dimension.ToString();
        set => Settings.Knowledge.Embedding.Dimension = ParsePositiveInt(value, Settings.Knowledge.Embedding.Dimension);
    }

    public string KnowledgeEmbeddingBatchSizeText
    {
        get => Settings.Knowledge.Embedding.BatchSize.ToString();
        set => Settings.Knowledge.Embedding.BatchSize = ParsePositiveInt(value, Settings.Knowledge.Embedding.BatchSize);
    }

    public string KnowledgeChunkTargetCharsText
    {
        get => Settings.Knowledge.Chunking.TargetChars.ToString();
        set => Settings.Knowledge.Chunking.TargetChars = ParsePositiveInt(value, Settings.Knowledge.Chunking.TargetChars);
    }

    public string KnowledgeChunkOverlapCharsText
    {
        get => Settings.Knowledge.Chunking.OverlapChars.ToString();
        set => Settings.Knowledge.Chunking.OverlapChars = ParseNonNegativeInt(value, Settings.Knowledge.Chunking.OverlapChars);
    }

    public string KnowledgeSearchTopKText
    {
        get => Settings.Knowledge.Search.TopK.ToString();
        set => Settings.Knowledge.Search.TopK = ParsePositiveInt(value, Settings.Knowledge.Search.TopK);
    }

    public string KnowledgeSearchMinScoreText
    {
        get => Settings.Knowledge.Search.MinScore.ToString("0.###");
        set => Settings.Knowledge.Search.MinScore = ParseDouble(value, Settings.Knowledge.Search.MinScore, 0, 1);
    }

    public string IgnoreDirectoriesText
    {
        get => string.Join(Environment.NewLine, Settings.WorkspaceIgnore.DirectoryNames);
        set => Settings.WorkspaceIgnore.DirectoryNames = ParseIgnoreDirectoryLines(value);
    }

    public string MemoryMaxTokensText
    {
        get => Settings.Memory.MaxMemoryTokens.ToString();
        set => Settings.Memory.MaxMemoryTokens = ParsePositiveInt(value, Settings.Memory.MaxMemoryTokens);
    }

    public string MemoryDailyRetentionDaysText
    {
        get => Settings.Memory.DailyFileRetentionDays.ToString();
        set => Settings.Memory.DailyFileRetentionDays = ParsePositiveInt(value, Settings.Memory.DailyFileRetentionDays);
    }

    public string MemoryConsolidationGapMinutesText
    {
        get => Math.Max(1, (int)Settings.Memory.ConsolidationMinGap.TotalMinutes).ToString();
        set => Settings.Memory.ConsolidationMinGap = TimeSpan.FromMinutes(ParsePositiveInt(value, 30));
    }

    public string ContextWindowTokensText
    {
        get => Settings.ContextCompaction.ContextWindowTokens.ToString();
        set => Settings.ContextCompaction.ContextWindowTokens = ParsePositiveInt(value, Settings.ContextCompaction.ContextWindowTokens);
    }

    public string CompactTriggerMessagesText
    {
        get => Settings.ContextCompaction.TriggerMessages.ToString();
        set => Settings.ContextCompaction.TriggerMessages = ParsePositiveInt(value, Settings.ContextCompaction.TriggerMessages);
    }

    public string CompactTargetUtilizationPercentText
    {
        get => (Settings.ContextCompaction.DynamicCompaction.TargetUtilization * 100).ToString("0");
        set => Settings.ContextCompaction.DynamicCompaction.TargetUtilization =
            ParsePercent(value, Settings.ContextCompaction.DynamicCompaction.TargetUtilization);
    }

    private static int? ParseOptionalPositiveInt(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return int.TryParse(text.Trim(), out var value) && value > 0 ? value : null;
    }

    private static List<string> ParseIgnoreDirectoryLines(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return text
            .Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int ParsePositiveInt(string? text, int fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        return int.TryParse(text.Trim(), out var value) && value > 0 ? value : fallback;
    }

    private static int ParseNonNegativeInt(string? text, int fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        return int.TryParse(text.Trim(), out var value) && value >= 0 ? value : fallback;
    }

    private static double ParseDouble(string? text, double fallback, double min, double max)
    {
        if (string.IsNullOrWhiteSpace(text) || !double.TryParse(text.Trim(), out var value))
        {
            return fallback;
        }

        return Math.Clamp(value, min, max);
    }

    private static double ParsePercent(string? text, double fallbackRatio)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallbackRatio;
        }

        var trimmed = text.Trim().TrimEnd('%');
        if (!double.TryParse(trimmed, out var percent))
        {
            return fallbackRatio;
        }

        return Math.Clamp(percent, 1, 99) / 100.0;
    }
}
