using Athlon.Agent.App.ViewModels;

namespace Athlon.Agent.App.Services.ComputerUse;

/// <summary>
/// Formats the lightweight Computer Use overlay status strip from chat messages.
/// </summary>
internal static class ComputerUseStatusFormatter
{
    internal const int AssistantSummaryMaxLength = 100;

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
