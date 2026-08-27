using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;

namespace Athlon.Agent.App.Services;

public enum TurnActivityKind
{
    Edited,
    Read,
    Searched,
    Explored,
    Command,
    Thought,
    /// <summary>Generic folded tool (memory_search, mcp_*, etc.).</summary>
    Tool,
    /// <summary>Intermediate model text folded into the turn activity.</summary>
    Narration
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
    string? Status = null,
    string? MessageId = null,
    string? ToolCallId = null);

public sealed record TurnActivitySummary
{
    public required int EditedFileCount { get; init; }

    public required int ExploredFileCount { get; init; }

    public required int SearchCount { get; init; }

    public required int CommandCount { get; init; }

    public required int ThoughtCount { get; init; }

    public required int TotalAdded { get; init; }

    public required int TotalRemoved { get; init; }

    public required IReadOnlyList<TurnActivityItem> Items { get; init; }

    /// <summary>Wall-clock ms for the sealed/live segment; 0 when unknown (e.g. history rebuild).</summary>
    public int DurationMs { get; init; }

    public bool HasContent =>
        Items.Count > 0
        || EditedFileCount > 0
        || ExploredFileCount > 0
        || SearchCount > 0
        || CommandCount > 0
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

    internal static readonly HashSet<string> CommandTools = new(StringComparer.Ordinal)
    {
        "execute_command"
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
        var commandCount = 0;
        var thoughtCount = 0;

        foreach (var message in turnMessages)
        {
            if (!message.IsTool && message.HasReasoning)
            {
                items.Add(CreateThoughtItem(message.ReasoningContent));
                thoughtCount++;
                if (!string.IsNullOrWhiteSpace(message.Content))
                {
                    items.Add(CreateNarrationItem(message.Content));
                }

                continue;
            }

            if (!message.IsTool)
            {
                if (!string.IsNullOrWhiteSpace(message.Content))
                {
                    items.Add(CreateNarrationItem(message.Content));
                }

                continue;
            }

            if (string.IsNullOrWhiteSpace(message.ToolName))
            {
                continue;
            }

            var statusKey = ToActivityStatus(message.ToolCallStatus);
            // Approval UI owns awaiting cards; do not fold them into activity.
            if (statusKey == "awaiting_approval")
            {
                continue;
            }

            var inFlight = statusKey is "preparing" or "running";
            var succeeded = statusKey == "succeeded";
            var toolName = message.ToolName;
            var args = message.ToolArgumentsText;

            var messageId = message.MessageId;
            var toolCallId = message.ToolCallId;
            var detailBody = ResolveActivityBody(message);

            // Successful edits render in FILES_CHANGED; show in-flight / failed edits here.
            if (EditTools.Contains(toolName))
            {
                if (succeeded)
                {
                    continue;
                }

                var editPath = ModifiedFilePathExtractor.ExtractPathFromArguments(args) ?? "…";
                items.Add(new TurnActivityItem(
                    TurnActivityKind.Edited,
                    inFlight ? "Writing" : "Edited",
                    editPath,
                    editPath,
                    Body: detailBody,
                    Status: statusKey,
                    MessageId: messageId,
                    ToolCallId: toolCallId));
                continue;
            }

            if (ReadTools.Contains(toolName))
            {
                var path = ModifiedFilePathExtractor.ExtractPathFromArguments(args) ?? (inFlight ? "…" : null);
                if (path is null)
                {
                    continue;
                }

                if (succeeded)
                {
                    exploredPaths.Add(path);
                }

                var range = path == "…" ? null : ExtractLineRange(args);
                var detail = range is null
                    ? path
                    : $"{path} L{range.Value.Start}-{range.Value.End}";
                items.Add(new TurnActivityItem(
                    TurnActivityKind.Read,
                    inFlight ? "Reading" : "Read",
                    detail,
                    path == "…" ? null : path,
                    Body: detailBody,
                    Status: statusKey,
                    MessageId: messageId,
                    ToolCallId: toolCallId));
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
                items.Add(new TurnActivityItem(
                    TurnActivityKind.Searched,
                    inFlight ? "Searching" : "Searched",
                    detail,
                    Body: detailBody,
                    Status: statusKey,
                    MessageId: messageId,
                    ToolCallId: toolCallId));
                continue;
            }

            if (ExploreTools.Contains(toolName))
            {
                var pattern = ExtractNamedArg(args, "pattern");
                var path = ModifiedFilePathExtractor.ExtractPathFromArguments(args) ?? (inFlight ? "…" : ".");
                var detail = pattern is null ? path : $"{Truncate(pattern, 40)} in {path}";
                if (succeeded && path != "…")
                {
                    exploredPaths.Add(path);
                }

                items.Add(new TurnActivityItem(
                    TurnActivityKind.Explored,
                    inFlight ? "Exploring" : "Explored",
                    detail,
                    path == "…" ? null : path,
                    Body: detailBody,
                    Status: statusKey,
                    MessageId: messageId,
                    ToolCallId: toolCallId));
                continue;
            }

            if (CommandTools.Contains(toolName))
            {
                commandCount++;
                var command = ExtractNamedArg(args, "command")
                    ?? FirstNonEmptyLine(args)
                    ?? (inFlight ? "…" : "execute_command");
                var detail = Truncate(FlattenWhitespace(command), 72);
                var body = !string.IsNullOrWhiteSpace(detailBody)
                    ? detailBody
                    : command;
                items.Add(new TurnActivityItem(
                    TurnActivityKind.Command,
                    inFlight ? "Running" : "Ran",
                    detail,
                    Body: body,
                    Status: statusKey,
                    MessageId: messageId,
                    ToolCallId: toolCallId));
                continue;
            }

            // Any other folded tool (memory_*, mcp_*, etc.)
            var genericDetail = !string.IsNullOrWhiteSpace(message.ToolSummary)
                ? Truncate(FlattenWhitespace(message.ToolSummary), 72)
                : Truncate(FlattenWhitespace(args), 72);
            if (string.IsNullOrWhiteSpace(genericDetail))
            {
                genericDetail = inFlight ? "…" : toolName;
            }

            items.Add(new TurnActivityItem(
                TurnActivityKind.Tool,
                toolName,
                genericDetail,
                Body: detailBody,
                Status: statusKey,
                MessageId: messageId,
                ToolCallId: toolCallId));
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
            CommandCount = commandCount,
            ThoughtCount = thoughtCount,
            TotalAdded = 0,
            TotalRemoved = 0,
            Items = items
        };
    }

    /// <summary>
    /// Reuses the replayed turn fold (file counts from transcript) and overlays live thought
    /// so a WebView reload does not replace a 39-file card with a shorter live snapshot.
    /// </summary>
    public static TurnActivitySummary OverlayLiveThought(TurnActivitySummary? replayed, TurnActivitySummary live)
    {
        if (replayed is null || !replayed.HasContent)
        {
            return live;
        }

        var liveThoughts = live.Items
            .Where(item => item.Kind == TurnActivityKind.Thought)
            .ToList();
        if (liveThoughts.Count == 0 && live.ThoughtCount == 0)
        {
            return replayed with { DurationMs = Math.Max(replayed.DurationMs, live.DurationMs) };
        }

        var items = replayed.Items
            .Where(item => item.Kind != TurnActivityKind.Thought)
            .ToList();
        items.AddRange(liveThoughts);
        return replayed with
        {
            ThoughtCount = Math.Max(replayed.ThoughtCount, Math.Max(live.ThoughtCount, liveThoughts.Count)),
            Items = items,
            DurationMs = Math.Max(replayed.DurationMs, live.DurationMs)
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

    private static TurnActivityItem CreateNarrationItem(string content)
    {
        var trimmed = content.Trim();
        var preview = Truncate(FirstLine(trimmed), 72);
        return new TurnActivityItem(
            TurnActivityKind.Narration,
            "Said",
            preview,
            Body: trimmed);
    }

    private static string? ResolveActivityBody(ChatMessageViewModel message)
    {
        if (!string.IsNullOrWhiteSpace(message.ToolDetail))
        {
            var args = message.ToolArgumentsText;
            if (string.IsNullOrWhiteSpace(args))
            {
                return message.ToolDetail;
            }

            return "Arguments:\n" + args.Trim() + "\n\nResult:\n" + message.ToolDetail.Trim();
        }

        if (!string.IsNullOrWhiteSpace(message.ToolArgumentsText))
        {
            return "Arguments:\n" + message.ToolArgumentsText.Trim();
        }

        return null;
    }

    private static string FirstLine(string text)
    {
        var newline = text.IndexOfAny(['\r', '\n']);
        return newline < 0 ? text : text[..newline].Trim();
    }

    private static string? FirstNonEmptyLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                return trimmed;
            }
        }

        return null;
    }

    private static string FlattenWhitespace(string value) =>
        string.Join(' ', value.Replace("\r\n", "\n").Split(['\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries));

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
