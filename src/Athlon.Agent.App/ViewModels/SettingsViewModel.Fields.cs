using System.Collections.ObjectModel;
using System.IO;
using Athlon.Agent.App.Localization;
using Athlon.Agent.App.Resources;
using Athlon.Agent.App.Services;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Audio;
using Athlon.Agent.Core.Knowledge;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Mcp;
using Athlon.Agent.Skills;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Athlon.Agent.App.ViewModels;

/// <summary>Text bindings over numeric and list settings.</summary>
public sealed partial class SettingsViewModel
{
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

    public string MaxToolScreenshotsInModelContextText
    {
        get => Settings.ContextCompaction.MaxToolScreenshotsInModelContext.ToString();
        set => Settings.ContextCompaction.MaxToolScreenshotsInModelContext =
            ParseNonNegativeInt(value, Settings.ContextCompaction.MaxToolScreenshotsInModelContext);
    }

    // ---- Text-to-speech (audio model) -------------------------------------

    public string TtsSpeedText
    {
        get => Settings.Tts.Speed.ToString("0.##");
        set => Settings.Tts.Speed = ParseDouble(value, Settings.Tts.Speed, 0.5, 2.0);
    }

    // ---- Computer Use (Phase 1/2 tunables) --------------------------------

    public string ComputerUseScreenshotLongestEdgeText
    {
        get => Settings.ComputerUse.ScreenshotMaxLongestEdge.ToString();
        set => Settings.ComputerUse.ScreenshotMaxLongestEdge =
            ParsePositiveInt(value, Settings.ComputerUse.ScreenshotMaxLongestEdge);
    }

    public string ComputerUseScreenshotJpegQualityText
    {
        get => Settings.ComputerUse.ScreenshotJpegQuality.ToString();
        set => Settings.ComputerUse.ScreenshotJpegQuality =
            ParsePositiveInt(value, Settings.ComputerUse.ScreenshotJpegQuality);
    }

    public string ComputerUseDefaultMaxTreeDepthText
    {
        get => Settings.ComputerUse.DefaultMaxTreeDepth.ToString();
        set => Settings.ComputerUse.DefaultMaxTreeDepth =
            ParsePositiveInt(value, Settings.ComputerUse.DefaultMaxTreeDepth);
    }

    public string ComputerUseDefaultMaxNodesText
    {
        get => Settings.ComputerUse.DefaultMaxNodes.ToString();
        set => Settings.ComputerUse.DefaultMaxNodes =
            ParsePositiveInt(value, Settings.ComputerUse.DefaultMaxNodes);
    }

    public string ComputerUseSettleSampleIntervalMsText
    {
        get => Settings.ComputerUse.SettleSampleIntervalMs.ToString();
        set => Settings.ComputerUse.SettleSampleIntervalMs =
            ParsePositiveInt(value, Settings.ComputerUse.SettleSampleIntervalMs);
    }

    public string ComputerUseSettleMinimumSamplesText
    {
        get => Settings.ComputerUse.SettleMinimumSamples.ToString();
        set => Settings.ComputerUse.SettleMinimumSamples =
            ParsePositiveInt(value, Settings.ComputerUse.SettleMinimumSamples);
    }

    public string ComputerUseUiaCallTimeoutMsText
    {
        get => Settings.ComputerUse.UiaCallTimeoutMs.ToString();
        set => Settings.ComputerUse.UiaCallTimeoutMs =
            ParsePositiveInt(value, Settings.ComputerUse.UiaCallTimeoutMs);
    }

    public string ComputerUseOverlayHideDelayMsText
    {
        get => Settings.ComputerUse.OverlayHideDelayMs.ToString();
        set => Settings.ComputerUse.OverlayHideDelayMs =
            ParseNonNegativeInt(value, Settings.ComputerUse.OverlayHideDelayMs);
    }

    /// <summary>History depth for full UI trees; older frames are collapsed to a summary.</summary>
    public string ComputerUseHistoryUiTreeRetentionText
    {
        get => Settings.ContextCompaction.RequestHistoryHygiene.HistoryUiTreeRetention.ToString();
        set => Settings.ContextCompaction.RequestHistoryHygiene.HistoryUiTreeRetention =
            ParseNonNegativeInt(value, Settings.ContextCompaction.RequestHistoryHygiene.HistoryUiTreeRetention);
    }

    /// <summary>
    /// Master switch for history UI-tree stripping. Defaults to off (see
    /// <see cref="RequestHistoryHygieneSettings.PruneHistoricalUiTree"/>), so the retention value
    /// above has no effect until the user opts in.
    /// </summary>
    public bool ComputerUsePruneHistoricalUiTree
    {
        get => Settings.ContextCompaction.RequestHistoryHygiene.PruneHistoricalUiTree;
        set
        {
            if (Settings.ContextCompaction.RequestHistoryHygiene.PruneHistoricalUiTree == value)
            {
                return;
            }

            Settings.ContextCompaction.RequestHistoryHygiene.PruneHistoricalUiTree = value;
            OnPropertyChanged();
        }
    }

    public string ComputerUseScreenshotRetentionMinutesText
    {
        get => Settings.ComputerUse.ScreenshotRetentionMinutes.ToString();
        set => Settings.ComputerUse.ScreenshotRetentionMinutes =
            ParseNonNegativeInt(value, Settings.ComputerUse.ScreenshotRetentionMinutes);
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
