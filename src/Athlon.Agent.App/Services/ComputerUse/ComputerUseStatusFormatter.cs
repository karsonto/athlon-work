using Athlon.Agent.App.ViewModels;

namespace Athlon.Agent.App.Services.ComputerUse;

/// <summary>
/// Formats the lightweight Computer Use overlay status strip from chat messages.
/// </summary>
internal static class ComputerUseStatusFormatter
{
    internal const int AssistantSummaryMaxLength = 100;

    /// <summary>Upper bound for the compact one-line action log rendered in the overlay.</summary>
    internal const int ActionLineMaxLength = 140;

    internal const string ActionLineSeparator = " · ";

    /// <summary>
    /// Builds the compact action line shown in the Computer Use overlay transcript, e.g.
    /// <c>computer_interact · click · element_native · 已完成</c>. The overlay cannot render the
    /// full tool card, so this is the only signal the user gets about what the agent is doing.
    /// </summary>
    internal static string FormatActionLine(
        string? toolName,
        string? argumentsText,
        string? resultContent,
        string? statusLabel)
    {
        var name = toolName?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        var parts = new List<string>(4) { name };
        var action = ExtractActionName(argumentsText);
        if (action.Length > 0)
        {
            parts.Add(action);
        }

        var resolvedVia = ExtractResolvedVia(resultContent);
        if (resolvedVia.Length > 0)
        {
            parts.Add(resolvedVia);
        }

        var status = statusLabel?.Trim();
        if (!string.IsNullOrEmpty(status))
        {
            parts.Add(status);
        }

        var line = string.Join(ActionLineSeparator, parts);
        return line.Length <= ActionLineMaxLength
            ? line
            : string.Concat(line.AsSpan(0, ActionLineMaxLength - 1), "…");
    }

    /// <summary>Pulls the <c>action</c> argument out of the persisted <c>key = value</c> argument block.</summary>
    internal static string ExtractActionName(string? argumentsText)
    {
        if (string.IsNullOrWhiteSpace(argumentsText))
        {
            return string.Empty;
        }

        foreach (var rawLine in argumentsText.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("action", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator < 0)
            {
                continue;
            }

            return line[(separator + 1)..].Trim();
        }

        return string.Empty;
    }

    /// <summary>
    /// Reads <c>resolved_via</c> from the observation envelope embedded in a completed tool result.
    /// Returns an empty string when the result carries no such field.
    /// </summary>
    internal static string ExtractResolvedVia(string? resultContent)
    {
        if (string.IsNullOrWhiteSpace(resultContent))
        {
            return string.Empty;
        }

        const string key = "\"resolved_via\"";
        var keyIndex = resultContent.IndexOf(key, StringComparison.Ordinal);
        if (keyIndex < 0)
        {
            return string.Empty;
        }

        var valueStart = resultContent.IndexOf('"', keyIndex + key.Length);
        if (valueStart < 0)
        {
            return string.Empty;
        }

        valueStart++;
        var valueEnd = resultContent.IndexOf('"', valueStart);
        return valueEnd <= valueStart ? string.Empty : resultContent[valueStart..valueEnd];
    }

    internal static string FormatToolLine(
        string? toolName,
        string? toolStatusLabel,
        string thinkingPlaceholder,
        string toolFormat)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return thinkingPlaceholder;
        }

        if (string.IsNullOrWhiteSpace(toolStatusLabel))
        {
            return toolName.Trim();
        }

        return string.Format(
            System.Globalization.CultureInfo.CurrentUICulture,
            toolFormat,
            toolName.Trim(),
            toolStatusLabel.Trim());
    }

    internal static string FormatAssistantSummary(string? content, int maxLength = AssistantSummaryMaxLength)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        var singleLine = CollapseWhitespace(content);
        if (maxLength <= 0 || singleLine.Length <= maxLength)
        {
            return singleLine;
        }

        if (maxLength == 1)
        {
            return "…";
        }

        return singleLine[..(maxLength - 1)] + "…";
    }

    internal static ChatMessageViewModel? FindLatestComputerUseTool(
        IReadOnlyList<ChatMessageViewModel> messages)
    {
        ChatMessageViewModel? anyTool = null;
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            var message = messages[index];
            if (!message.IsTool || string.IsNullOrWhiteSpace(message.ToolName))
            {
                continue;
            }

            anyTool ??= message;
            if (message.ToolName.StartsWith("computer_", StringComparison.OrdinalIgnoreCase))
            {
                return message;
            }
        }

        return anyTool;
    }

    internal static ChatMessageViewModel? FindLatestAssistantWithContent(
        IReadOnlyList<ChatMessageViewModel> messages)
    {
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            var message = messages[index];
            if (message.IsUser
                || message.IsTool
                || message.IsCompaction
                || message.IsHiddenPlaceholder)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(message.Content))
            {
                return message;
            }
        }

        return null;
    }

    private static string CollapseWhitespace(string value)
    {
        var buffer = new char[value.Length];
        var length = 0;
        var pendingSpace = false;
        foreach (var ch in value.Trim())
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = length > 0;
                continue;
            }

            if (pendingSpace)
            {
                buffer[length++] = ' ';
                pendingSpace = false;
            }

            buffer[length++] = ch;
        }

        return new string(buffer, 0, length);
    }
}
