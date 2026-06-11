namespace Athlon.Agent.Core.Compaction;

public sealed record CompactionAuditDisplayInfo(
    string CardTitle,
    string StrategySubtitle,
    string Summary,
    string Detail);

public static class CompactionAuditDisplay
{
    public static CompactionAuditDisplayInfo Parse(string content)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n');
        var legacyKind = string.Empty;
        var strategyToken = string.Empty;
        var layersToken = string.Empty;
        var pressureToken = string.Empty;
        var utilizationToken = string.Empty;
        var summaryInputBeforeToken = string.Empty;
        var summaryInputAfterToken = string.Empty;
        var hygieneSavingsToken = string.Empty;
        var summary = string.Empty;

        foreach (var line in lines)
        {
            if (line.StartsWith("CompactionKind:", StringComparison.OrdinalIgnoreCase))
            {
                legacyKind = line["CompactionKind:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("CompactionStrategy:", StringComparison.OrdinalIgnoreCase))
            {
                strategyToken = line["CompactionStrategy:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("CompactionLayers:", StringComparison.OrdinalIgnoreCase))
            {
                layersToken = line["CompactionLayers:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("ContextPressure:", StringComparison.OrdinalIgnoreCase))
            {
                pressureToken = line["ContextPressure:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("ContextUtilization:", StringComparison.OrdinalIgnoreCase))
            {
                utilizationToken = line["ContextUtilization:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("SummaryInputCharsBefore:", StringComparison.OrdinalIgnoreCase))
            {
                summaryInputBeforeToken = line["SummaryInputCharsBefore:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("SummaryInputCharsAfter:", StringComparison.OrdinalIgnoreCase))
            {
                summaryInputAfterToken = line["SummaryInputCharsAfter:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("HygieneSavingsEstimate:", StringComparison.OrdinalIgnoreCase))
            {
                hygieneSavingsToken = line["HygieneSavingsEstimate:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("Summary:", StringComparison.OrdinalIgnoreCase))
            {
                summary = line["Summary:".Length..].Trim();
            }
        }

        var strategy = ResolveStrategy(strategyToken, legacyKind);
        var layers = ParseLayers(layersToken, strategy);
        var cardTitle = GetCardTitle(strategy);
        var subtitle = BuildStrategySubtitle(
            strategy,
            layers,
            pressureToken,
            utilizationToken,
            summaryInputBeforeToken,
            summaryInputAfterToken,
            hygieneSavingsToken);

        return new CompactionAuditDisplayInfo(
            cardTitle,
            subtitle,
            summary,
            content.Trim());
    }

    public static string FormatStrategy(CompactionStrategy strategy) =>
        strategy switch
        {
            CompactionStrategy.ForceCompact => "force_compact",
            CompactionStrategy.ManualCompact => "manual_compact",
            _ => "conversation_compact",
        };

    public static string FormatLayers(IEnumerable<CompactionLayer> layers) =>
        string.Join(
            ',',
            layers
                .Distinct()
                .OrderBy(layer => layer)
                .Select(layer => layer switch
                {
                    CompactionLayer.TruncateArgs => "truncate_args",
                    CompactionLayer.ToolResultEviction => "tool_result_eviction",
                    _ => "conversation_compact",
                }));

    public static string GetCardTitle(CompactionStrategy strategy) =>
        strategy switch
        {
            CompactionStrategy.ForceCompact => "③ 强制对话压缩",
            CompactionStrategy.ManualCompact => "③ 手动对话压缩",
            _ => "③ 对话压缩（LLM 摘要）",
        };

    private static CompactionStrategy ResolveStrategy(string strategyToken, string legacyKind)
    {
        if (!string.IsNullOrWhiteSpace(strategyToken))
        {
            return strategyToken.ToLowerInvariant() switch
            {
                "force_compact" => CompactionStrategy.ForceCompact,
                "manual_compact" => CompactionStrategy.ManualCompact,
                _ => CompactionStrategy.ConversationCompact,
            };
        }

        return legacyKind.ToLowerInvariant() switch
        {
            "manualcompact" => CompactionStrategy.ManualCompact,
            "forcecompact" => CompactionStrategy.ForceCompact,
            "microcompact" => CompactionStrategy.ConversationCompact,
            "autocompact" => CompactionStrategy.ConversationCompact,
            _ => CompactionStrategy.ConversationCompact,
        };
    }

    private static IReadOnlyList<CompactionLayer> ParseLayers(string layersToken, CompactionStrategy strategy)
    {
        if (string.IsNullOrWhiteSpace(layersToken))
        {
            return strategy switch
            {
                CompactionStrategy.ManualCompact => [CompactionLayer.ConversationCompact],
                CompactionStrategy.ForceCompact => [CompactionLayer.ConversationCompact],
                _ => [CompactionLayer.ConversationCompact],
            };
        }

        var layers = new List<CompactionLayer>();
        foreach (var part in layersToken.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "truncate_args":
                    layers.Add(CompactionLayer.TruncateArgs);
                    break;
                case "tool_result_eviction":
                    layers.Add(CompactionLayer.ToolResultEviction);
                    break;
                case "conversation_compact":
                    layers.Add(CompactionLayer.ConversationCompact);
                    break;
            }
        }

        return layers.Count == 0 ? [CompactionLayer.ConversationCompact] : layers;
    }

    private static string BuildStrategySubtitle(
        CompactionStrategy strategy,
        IReadOnlyList<CompactionLayer> layers,
        string pressureToken,
        string utilizationToken,
        string summaryInputBeforeToken,
        string summaryInputAfterToken,
        string hygieneSavingsToken)
    {
        var trigger = strategy switch
        {
            CompactionStrategy.ForceCompact => "触发：模型上下文超限后强制压缩",
            CompactionStrategy.ManualCompact => "触发：用户手动压缩",
            _ => "触发：动态预算 / 消息数阈值",
        };

        var layerText = string.Join(" → ", layers.Select(GetLayerLabel));
        var pressureText = string.IsNullOrWhiteSpace(pressureToken) ? null : $"压力 {pressureToken}";
        var utilizationText = string.IsNullOrWhiteSpace(utilizationToken) ? null : $"利用率 {utilizationToken}";
        var summaryText = string.IsNullOrWhiteSpace(summaryInputBeforeToken) || string.IsNullOrWhiteSpace(summaryInputAfterToken)
            ? null
            : $"摘要输入 {summaryInputBeforeToken}→{summaryInputAfterToken}";
        var hygieneText = string.IsNullOrWhiteSpace(hygieneSavingsToken) ? null : $"hygiene ~{hygieneSavingsToken} tok";
        var metrics = string.Join(
            " · ",
            new[] { pressureText, utilizationText, summaryText, hygieneText }.Where(part => !string.IsNullOrWhiteSpace(part)));
        return string.IsNullOrWhiteSpace(metrics)
            ? $"{trigger} · 层级：{layerText}"
            : $"{trigger} · {metrics} · 层级：{layerText}";
    }

    private static string GetLayerLabel(CompactionLayer layer) =>
        layer switch
        {
            CompactionLayer.TruncateArgs => "② 工具参数截断",
            CompactionLayer.ToolResultEviction => "① 工具结果归档",
            _ => "③ LLM 对话摘要",
        };
}
