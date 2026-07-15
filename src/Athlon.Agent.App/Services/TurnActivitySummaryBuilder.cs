using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;

namespace Athlon.Agent.App.Services;

public enum TurnActivityKind
{
    Edited,
    Read,
    Searched,
    Explored,
    Thought
}

public sealed record TurnActivityDiffLine(string Kind, string Text, int? Count = null);

public sealed record TurnActivityItem(
    TurnActivityKind Kind,
    string Verb,
    string Detail,
    string? Path = null,
    int Added = 0,
    int Removed = 0,
    IReadOnlyList<TurnActivityDiffLine>? DiffLines = null,
    string? Body = null,
    string? Status = null);

public sealed class TurnActivitySummary
{
    public required int EditedFileCount { get; init; }
    public required int ExploredFileCount { get; init; }
    public required int SearchCount { get; init; }
    public required int ThoughtCount { get; init; }
    public required int TotalAdded { get; init; }
    public required int TotalRemoved { get; init; }
    public required IReadOnlyList<TurnActivityItem> Items { get; init; }

    public bool HasContent =>
        Items.Count > 0
        || EditedFileCount > 0
        || ExploredFileCount > 0
        || SearchCount > 0
        || ThoughtCount > 0;
}

/// <summary>Builds a Cursor-style per-turn activity summary from chat tool messages.</summary>
public static class TurnActivitySummaryBuilder
{
    internal static readonly HashSet<string> EditTools = new(StringComparer.Ordinal)
    {
        "file_edit",
        "file_write",
        "apply_patch"
    };

    internal static readonly HashSet<string> ReadTools = new(StringComparer.Ordinal)
    {
        "file_read"
    };

    internal static readonly HashSet<string> SearchTools = new(StringComparer.Ordinal)
    {
        "grep_files"
    };

    internal static readonly HashSet<string> ExploreTools = new(StringComparer.Ordinal)
    {
        "glob_files",
        "file_list"
    };

    public static IReadOnlyList<TurnActivitySummary> BuildTurnSummariesFromChatMessages(
        IReadOnlyList<ChatMessage> messages)
    {
        var viewModels = messages
            .Where(message => message.Role is MessageRole.User or MessageRole.Tool or MessageRole.Assistant)
            .Select(message => new ChatMessageViewModel(message))
            .ToList();
        return BuildTurnSummaries(viewModels);
    }


    public static IReadOnlyList<TurnActivitySummary> BuildTurnSummaries(
        IReadOnlyList<ChatMessageViewModel> messages)
    {
        var summaries = new List<TurnActivitySummary>();
        var current = new List<ChatMessageViewModel>();

        void Flush()
        {
            var summary = Build(current);
            if (summary is { HasContent: true })
            {
                summaries.Add(summary);
            }

            current.Clear();
        }

        foreach (var message in messages)
        {
            if (message.IsUser)
            {
                Flush();
                continue;
            }

            current.Add(message);
        }

        Flush();
        return summaries;
    }

    public static TurnActivitySummary? Build(IReadOnlyList<ChatMessageViewModel> turnMessages)
    {
        var items = new List<TurnActivityItem>();
        var exploredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var searchCount = 0;
        var thoughtCount = 0;

        foreach (var message in turnMessages)
        {
            if (!message.IsTool && message.HasReasoning)
            {
                items.Add(CreateThoughtItem(message.ReasoningContent));
                thoughtCount++;
                continue;
            }

            if (!message.IsTool || string.IsNullOrWhiteSpace(message.ToolName))
            {
                continue;
            }

            var statusKey = ToActivityStatus(message.ToolCallStatus);
            // Skip tools still in-flight; completed outcomes (incl. failed) are listed.
            if (statusKey is "preparing" or "running" or "awaiting_approval")
            {
                continue;
            }

            var succeeded = statusKey == "succeeded";
            var toolName = message.ToolName;
            var args = message.ToolArgumentsText;

            // File edits render in a separate FILES_CHANGED bubble.
            if (EditTools.Contains(toolName))
            {
                continue;
            }

            if (ReadTools.Contains(toolName))
            {
                var path = ModifiedFilePathExtractor.ExtractPathFromArguments(args);
                if (path is null)
                {
                    continue;
                }

                if (succeeded)
                {
                    exploredPaths.Add(path);
                }

                var range = ExtractLineRange(args);
                var detail = range is null
                    ? path
                    : $"{path} L{range.Value.Start}-{range.Value.End}";
                items.Add(new TurnActivityItem(TurnActivityKind.Read, "Read", detail, path, Status: statusKey));
                continue;
            }

            if (SearchTools.Contains(toolName))
            {
                if (succeeded)
                {
                    searchCount++;
                }

                var pattern = ExtractNamedArg(args, "pattern") ?? "…";
                var scope = ModifiedFilePathExtractor.ExtractPathFromArguments(args)
                    ?? ExtractNamedArg(args, "glob")
                    ?? ".";
                var detail = $"{Truncate(pattern, 48)} in {scope}";
                items.Add(new TurnActivityItem(TurnActivityKind.Searched, "Searched", detail, Status: statusKey));
                continue;
            }

            if (ExploreTools.Contains(toolName))
            {
                var pattern = ExtractNamedArg(args, "pattern");
                var path = ModifiedFilePathExtractor.ExtractPathFromArguments(args) ?? ".";
                var detail = pattern is null ? path : $"{Truncate(pattern, 40)} in {path}";
                if (succeeded)
                {
                    exploredPaths.Add(path);
                }

                items.Add(new TurnActivityItem(TurnActivityKind.Explored, "Explored", detail, path, Status: statusKey));
            }
        }

        if (items.Count == 0)
        {
            return null;
        }

        return new TurnActivitySummary
        {
            EditedFileCount = 0,
            ExploredFileCount = exploredPaths.Count,
            SearchCount = searchCount,
            ThoughtCount = thoughtCount,
            TotalAdded = 0,
            TotalRemoved = 0,
            Items = items
        };
    }

    internal static string ToActivityStatus(ToolCallDisplayStatus status) => status switch
    {
        ToolCallDisplayStatus.Preparing => "preparing",
        ToolCallDisplayStatus.Running => "running",
        ToolCallDisplayStatus.AwaitingApproval => "awaiting_approval",
        ToolCallDisplayStatus.ApprovalDenied => "approval_denied",
        ToolCallDisplayStatus.Failed => "failed",
        ToolCallDisplayStatus.Cancelled => "cancelled",
        ToolCallDisplayStatus.Succeeded => "succeeded",
        _ => "succeeded"
    };

    private static TurnActivityItem CreateThoughtItem(string reasoning)
    {
        var trimmed = reasoning.Trim();
        var preview = Truncate(FirstLine(trimmed), 72);
        return new TurnActivityItem(
            TurnActivityKind.Thought,
            "Thought",
            preview,
            Body: trimmed);
    }

    private static string FirstLine(string text)
    {
        var newline = text.IndexOfAny(['\r', '\n']);
        return newline < 0 ? text : text[..newline].Trim();
    }

    private static (int Start, int End)? ExtractLineRange(string? args)
    {
        var start = TryParseIntArg(args, "start_line");
        var end = TryParseIntArg(args, "end_line");
        if (start is null && end is null)
        {
            return null;
        }

        var s = start ?? 1;
        var e = end ?? s;
        return (s, e);
    }

    private static int? TryParseIntArg(string? args, string name)
    {
        var raw = ExtractNamedArg(args, name);
        return int.TryParse(raw, out var value) ? value : null;
    }

    private static string? ExtractNamedArg(string? argumentsText, string name)
    {
        if (string.IsNullOrWhiteSpace(argumentsText))
        {
            return null;
        }

        if (ToolCallStreamingJsonHelper.TryExtractStringProperty(argumentsText, name, out var jsonValue)
            && !string.IsNullOrWhiteSpace(jsonValue))
        {
            return jsonValue;
        }

        foreach (var line in argumentsText.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith(name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var separator = trimmed.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = trimmed[..separator].Trim();
            if (!string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return trimmed[(separator + 1)..].Trim().Trim('"');
        }

        return null;
    }

    private static string Truncate(string value, int max)
    {
        if (value.Length <= max)
        {
            return value;
        }

        return value[..(max - 1)] + "…";
    }
}
