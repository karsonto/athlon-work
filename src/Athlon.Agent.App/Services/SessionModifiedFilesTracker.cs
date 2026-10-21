using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Streaming;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.App.Services;

/// <summary>
/// Live state for the current turn's file edits.
///
/// <para>Successful edits are staged per tool call, then published as one turn summary card.
/// Paths touched more than once are merged (later <c>file_write</c> replaces, edits append).
/// The path set also anchors the live turn surface so a refresh/reload is deferred instead of
/// stacking a twin card.</para>
/// </summary>
public sealed class SessionModifiedFilesTracker
{
    private readonly Dictionary<string, string> _toolCallIdToName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _toolCallIdToArgs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _currentTurnPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<EditFileCard> _segmentEditCards = new();

    /// <summary>One completed file edit, kept so a repeated result for the same call can replace it.</summary>
    public sealed record EditFileCard(string ToolCallId, IReadOnlyList<ModifiedFileViewModel> Files);

    /// <summary>
    /// True while the current turn has touched a file. This is the live-turn gate: a full replay
    /// while it holds would re-emit cards the live publish already rendered.
    /// </summary>
    public bool HasCurrentTurnPaths => _currentTurnPaths.Count > 0;

    public void Clear()
    {
        _toolCallIdToName.Clear();
        _toolCallIdToArgs.Clear();
        _currentTurnPaths.Clear();
        _segmentEditCards.Clear();
    }

    /// <summary>Starts a new user turn: drop prior-turn file entries so the state is per-turn only.</summary>
    public void BeginTurn()
    {
        _currentTurnPaths.Clear();
        _segmentEditCards.Clear();
    }

    /// <summary>
    /// Completed edits for the current segment, in the order they finished. Each entry is one card
    /// on the timeline, keyed by its tool call id, so a live publish and its replay resolve to the
    /// same entry id. Not destructive: a rebuild re-publishes the same list and the stable key makes
    /// the upsert idempotent.
    /// </summary>
    public IReadOnlyList<EditFileCard> PeekSegmentEditCards() => _segmentEditCards;

    /// <summary>This turn's edits merged by path, in first-seen order. This is the summary card payload.</summary>
    public IReadOnlyList<ModifiedFileViewModel> PeekTurnFiles() =>
        MergeByPath(_segmentEditCards.SelectMany(card => card.Files));

    /// <summary>
    /// Collapses edits onto one view model per path. A later <c>file_write</c> replaces the diff;
    /// other edits append.
    /// </summary>
    public static IReadOnlyList<ModifiedFileViewModel> MergeByPath(IEnumerable<ModifiedFileViewModel> files)
    {
        var order = new List<ModifiedFileViewModel>();
        var byPath = new Dictionary<string, ModifiedFileViewModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (string.IsNullOrWhiteSpace(file.RelativePath))
            {
                continue;
            }

            if (!byPath.TryGetValue(file.RelativePath, out var existing))
            {
                var copy = new ModifiedFileViewModel(file.RelativePath, file.ToolName, file.Status);
                if (file.HasDiff)
                {
                    copy.SetDiff(file.UnifiedDiffText);
                }

                byPath[file.RelativePath] = copy;
                order.Add(copy);
                continue;
            }

            existing.SetToolName(file.ToolName);
            if (!file.HasDiff)
            {
                continue;
            }

            if (string.Equals(file.ToolName, "file_write", StringComparison.Ordinal))
            {
                existing.SetDiff(file.UnifiedDiffText);
            }
            else
            {
                existing.AppendDiff(file.UnifiedDiffText);
            }
        }

        return order;
    }

    /// <summary>
    /// Single-edit card payload for a succeeded file tool: one file for <c>file_edit</c> /
    /// <c>file_write</c>, one per touched path for <c>apply_patch</c>. Replay and the live tracker
    /// share this so both paths render the identical card.
    /// </summary>
    public static IReadOnlyList<ModifiedFileViewModel> BuildEditCardFiles(ChatMessageViewModel message)
    {
        if (!message.IsTool
            || !ModifiedFilePathExtractor.IsFileTool(message.ToolName)
            || ModifiedFilePathExtractor.ToModifiedFileStatus(message.ToolCallStatus) != ModifiedFileStatus.Succeeded)
        {
            return Array.Empty<ModifiedFileViewModel>();
        }

        return BuildEditCardFiles(message.ToolName, message.ToolArgumentsText, message.Content);
    }

    public static IReadOnlyList<ModifiedFileViewModel> BuildEditCardFiles(
        string? toolName,
        string? argumentsText,
        string? toolContent)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return Array.Empty<ModifiedFileViewModel>();
        }

        var files = new List<ModifiedFileViewModel>();
        if (string.Equals(toolName, "apply_patch", StringComparison.Ordinal))
        {
            foreach (var path in ModifiedFilePathExtractor.ExtractApplyPatchPaths(toolContent ?? string.Empty))
            {
                var patchItem = new ModifiedFileViewModel(path, toolName, ModifiedFileStatus.Succeeded);
                TryAttachDiff(patchItem, toolName, argumentsText, toolContent);
                files.Add(patchItem);
            }

            return files;
        }

        var relativePath = ModifiedFilePathExtractor.ExtractPathFromArguments(argumentsText);
        if (relativePath is null && !string.IsNullOrWhiteSpace(toolContent))
        {
            ToolMessageDisplayParser.ParseToolContent(
                toolContent,
                out _,
                out _,
                out _,
                out _,
                out _,
                out var embeddedArguments,
                out _);
            relativePath = ModifiedFilePathExtractor.ExtractPathFromArguments(embeddedArguments);
        }

        if (relativePath is not null)
        {
            var item = new ModifiedFileViewModel(relativePath, toolName, ModifiedFileStatus.Succeeded);
            TryAttachDiff(item, toolName, argumentsText, toolContent);
            files.Add(item);
        }

        return files;
    }

    /// <summary>
    /// Files changed in one turn. Tool results supply edit diffs; a succeeded
    /// <c>file_write</c> whose stored arguments lead with the file body (so the path is not on
    /// the first line) is recovered from the assistant tool call.
    /// </summary>
    public static IReadOnlyList<ModifiedFileViewModel> CollectTurnFiles(IReadOnlyList<ChatMessage> turnBody)
    {
        var files = new List<ModifiedFileViewModel>();
        var succeededCallIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in turnBody)
        {
            if (message.Role != MessageRole.Tool)
            {
                continue;
            }

            var viewModel = new ChatMessageViewModel(message);
            if (!ChatTimelineProjector.IsSucceededFileEdit(viewModel))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(viewModel.ToolCallId))
            {
                succeededCallIds.Add(viewModel.ToolCallId);
            }

            files.AddRange(BuildEditCardFiles(viewModel));
        }

        foreach (var message in turnBody)
        {
            if (message.Role != MessageRole.Assistant || string.IsNullOrWhiteSpace(message.ToolCallsJson))
            {
                continue;
            }

            var calls = AssistantToolCallsCodec.Deserialize(message.ToolCallsJson);
            if (calls is null)
            {
                continue;
            }

            foreach (var call in calls)
            {
                if (!succeededCallIds.Contains(call.Id)
                    || !ModifiedFilePathExtractor.IsFileTool(call.Name))
                {
                    continue;
                }

                EnsureFileFromAssistantCall(files, call);
            }
        }

        return MergeByPath(files);
    }

    private static void EnsureFileFromAssistantCall(List<ModifiedFileViewModel> files, AgentToolCall call)
    {
        if (!call.Arguments.TryGetString(ToolPathNormalizer.PathArgumentName, out var path)
            || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var normalized = ToolPathNormalizer.ForModel(path);
        var existing = files.FirstOrDefault(file =>
            string.Equals(file.RelativePath, normalized, StringComparison.OrdinalIgnoreCase));
        if (!string.Equals(call.Name, "file_write", StringComparison.Ordinal)
            || !call.Arguments.TryGetString("content", out var content)
            || string.IsNullOrEmpty(content))
        {
            if (existing is null)
            {
                files.Add(new ModifiedFileViewModel(normalized, call.Name, ModifiedFileStatus.Succeeded));
            }

            return;
        }

        if (existing is null)
        {
            existing = new ModifiedFileViewModel(normalized, call.Name, ModifiedFileStatus.Succeeded);
            files.Add(existing);
        }

        if (!existing.HasDiff)
        {
            existing.SetDiff(UnifiedDiffGenerator.Generate(string.Empty, content, existing.RelativePath));
        }
    }

    public void Process(AgentStreamEvent streamEvent)
    {
        switch (streamEvent)
        {
            case AgentStreamEvent.ToolCallStart(var toolCallId, var toolName, _):
                _toolCallIdToName[toolCallId] = toolName;
                break;
            case AgentStreamEvent.ToolCallArgs(var toolCallId, var argsJson):
                _toolCallIdToArgs[toolCallId] = argsJson;
                if (_toolCallIdToName.TryGetValue(toolCallId, out var argsToolName)
                    && ModifiedFilePathExtractor.IsFileTool(argsToolName))
                {
                    var path = ModifiedFilePathExtractor.ExtractPathFromArguments(argsJson);
                    if (path is not null)
                    {
                        TrackPath(path);
                    }
                }

                break;
            case AgentStreamEvent.ToolCallEnd(var toolCallId):
                if (_toolCallIdToName.TryGetValue(toolCallId, out var endToolName)
                    && _toolCallIdToArgs.TryGetValue(toolCallId, out var endArgs)
                    && ModifiedFilePathExtractor.IsFileTool(endToolName))
                {
                    var path = ModifiedFilePathExtractor.ExtractPathFromArguments(endArgs);
                    if (path is not null)
                    {
                        TrackPath(path);
                    }
                }

                break;
            case AgentStreamEvent.ToolCallResult(var toolCallId, var content, _, _):
                HandleToolCallResult(toolCallId, content);
                _toolCallIdToName.Remove(toolCallId);
                _toolCallIdToArgs.Remove(toolCallId);
                break;
        }
    }

    /// <summary>
    /// Rebuilds live tracking from the <em>current turn only</em> (messages after the last
    /// user / visible compaction). Prior turns are rendered by FILES_CHANGED replay; restoring
    /// them here would make RestoreLive upsert overwrite a replayed card with stale paths.
    /// </summary>
    public void RebuildFromMessages(IReadOnlyList<ChatMessageViewModel> messages)
    {
        Clear();
        var start = IndexAfterLastTurnBoundary(messages);
        for (var i = start; i < messages.Count; i++)
        {
            var message = messages[i];
            if (!message.IsTool || !ModifiedFilePathExtractor.IsFileTool(message.ToolName))
            {
                continue;
            }

            var status = ModifiedFilePathExtractor.ToModifiedFileStatus(message.ToolCallStatus);
            var argsText = message.ToolArgumentsText;

            // Succeeded edits re-publish as their own timeline cards after a reload; restoring them
            // here is what lets RestoreLiveTurnCardsAfterReload rewrite the replayed cards in place.
            if (ChatTimelineProjector.IsSucceededFileEdit(message))
            {
                var cardFiles = BuildEditCardFiles(message.ToolName, argsText, message.Content);
                if (cardFiles.Count > 0 && !string.IsNullOrWhiteSpace(message.ToolCallId))
                {
                    _segmentEditCards.RemoveAll(
                        card => string.Equals(card.ToolCallId, message.ToolCallId, StringComparison.Ordinal));
                    _segmentEditCards.Add(new EditFileCard(message.ToolCallId, cardFiles));
                }
            }

            if (string.Equals(message.ToolName, "apply_patch", StringComparison.Ordinal)
                && status == ModifiedFileStatus.Succeeded)
            {
                foreach (var path in ModifiedFilePathExtractor.ExtractApplyPatchPaths(message.Content))
                {
                    TrackPath(path);
                }

                continue;
            }

            var relativePath = ModifiedFilePathExtractor.ExtractPathFromArguments(argsText);
            if (relativePath is not null)
            {
                TrackPath(relativePath);
            }
        }
    }

    /// <summary>Index of the first message belonging to the current turn (after last user/compaction).</summary>
    internal static int IndexAfterLastTurnBoundary(IReadOnlyList<ChatMessageViewModel> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].IsUser)
            {
                return i + 1;
            }

            if (messages[i].IsCompaction
                && ChatDisplayPolicy.ShouldDisplayCompactionCheckpoint(messages[i]))
            {
                return i + 1;
            }
        }

        return 0;
    }

    private void HandleToolCallResult(string toolCallId, string content)
    {
        _toolCallIdToName.TryGetValue(toolCallId, out var toolName);
        _toolCallIdToArgs.TryGetValue(toolCallId, out var args);
        if (string.IsNullOrWhiteSpace(toolName))
        {
            ToolMessageDisplayParser.ParseToolContent(
                content,
                out _,
                out toolName,
                out _,
                out _,
                out _,
                out _,
                out _);
        }

        if (!ModifiedFilePathExtractor.IsFileTool(toolName))
        {
            return;
        }

        var status = ModifiedFilePathExtractor.ParseResultStatus(content);
        if (status == ModifiedFileStatus.Succeeded)
        {
            var cardFiles = BuildEditCardFiles(toolName, args, content);
            if (cardFiles.Count > 0 && !string.IsNullOrWhiteSpace(toolCallId))
            {
                // Replace a re-emitted card for the same call (retry / duplicate result) instead of
                // stacking a second card for one edit.
                _segmentEditCards.RemoveAll(card => string.Equals(card.ToolCallId, toolCallId, StringComparison.Ordinal));
                _segmentEditCards.Add(new EditFileCard(toolCallId, cardFiles));
            }
        }

        if (string.Equals(toolName, "apply_patch", StringComparison.Ordinal))
        {
            var paths = ModifiedFilePathExtractor.ExtractApplyPatchPaths(content);
            if (paths.Count > 0)
            {
                foreach (var path in paths)
                {
                    TrackPath(path);
                }

                return;
            }
        }

        var relativePath = ModifiedFilePathExtractor.ExtractPathFromArguments(args);
        if (relativePath is null && !string.IsNullOrWhiteSpace(content))
        {
            ToolMessageDisplayParser.ParseToolContent(
                content,
                out _,
                out _,
                out _,
                out _,
                out _,
                out var argumentsText,
                out _);
            relativePath = ModifiedFilePathExtractor.ExtractPathFromArguments(argumentsText);
        }

        if (relativePath is not null)
        {
            TrackPath(relativePath);
        }
    }

    /// <summary>Records a touched path so the turn keeps its live surface until replay owns it.</summary>
    private void TrackPath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return;
        }

        _currentTurnPaths.Add(ToolPathNormalizer.ForModel(relativePath));
    }

    private static void TryAttachDiff(
        ModifiedFileViewModel file,
        string? toolName,
        string? argumentsText,
        string? toolContent)
    {
        var diff = ExtractDiff(toolName, argumentsText, toolContent, file.RelativePath);
        if (string.IsNullOrWhiteSpace(diff))
        {
            return;
        }

        // Whole-file rewrites replace; incremental edits accumulate.
        if (string.Equals(toolName, "file_write", StringComparison.Ordinal))
        {
            file.SetDiff(diff);
        }
        else
        {
            file.AppendDiff(diff);
        }
    }

    internal static string? ExtractDiff(
        string? toolName,
        string? argumentsText,
        string? toolContent,
        string relativePath)
    {
        if (string.Equals(toolName, "file_edit", StringComparison.Ordinal))
        {
            var body = StripToolResultBody(toolContent);
            return LooksLikeUnifiedDiff(body) ? body : null;
        }

        if (string.Equals(toolName, "file_write", StringComparison.Ordinal))
        {
            // Persisted results keep the full body after "content=", spanning lines until Summary.
            // The display arguments line only has the path, so the review pane has to read the body.
            if (TryExtractPersistedFileWriteContent(toolContent, out var persisted)
                && !IsFileWriteContentPlaceholder(persisted))
            {
                return UnifiedDiffGenerator.Generate(string.Empty, persisted, relativePath);
            }

            if (TryExtractFileWriteContent(argumentsText, out var content)
                && !IsFileWriteContentPlaceholder(content))
            {
                return UnifiedDiffGenerator.Generate(string.Empty, content, relativePath);
            }

            return null;
        }

        if (string.Equals(toolName, "apply_patch", StringComparison.Ordinal))
        {
            if (TryExtractPatchArgument(argumentsText, out var patch) && LooksLikeUnifiedDiff(patch))
            {
                return patch;
            }

            var body = StripToolResultBody(toolContent);
            return LooksLikeUnifiedDiff(body) ? body : null;
        }

        return null;
    }

    /// <summary>Mirrors <c>ModelMessageBuilder.StripToolCallIdAndMetadata</c> for App-layer use.</summary>
    private static string StripToolResultBody(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        var lines = content.Split(["\r\n", "\n"], StringSplitOptions.None);
        var startIndex = 0;

        if (lines.Length > startIndex && lines[startIndex].StartsWith("ToolCallId:", StringComparison.OrdinalIgnoreCase))
            startIndex++;
        if (lines.Length > startIndex && lines[startIndex].StartsWith("Tool `", StringComparison.Ordinal))
            startIndex++;
        if (lines.Length > startIndex && lines[startIndex].Length == 0)
            startIndex++;
        if (lines.Length > startIndex && lines[startIndex].StartsWith("Arguments:", StringComparison.OrdinalIgnoreCase))
            startIndex++;
        if (lines.Length > startIndex && lines[startIndex].StartsWith("Summary:", StringComparison.OrdinalIgnoreCase))
            startIndex++;
        if (lines.Length > startIndex && lines[startIndex].Length == 0)
            startIndex++;

        if (startIndex >= lines.Length)
        {
            return string.Empty;
        }

        return string.Join(Environment.NewLine, lines[startIndex..]);
    }

    private static bool TryExtractFileWriteContent(string? argumentsText, out string content)
    {
        content = string.Empty;
        if (string.IsNullOrWhiteSpace(argumentsText))
        {
            return false;
        }

        if (ToolCallStreamingJsonHelper.TryParseCompleteFileWriteArgs(argumentsText, out _, out content)
            && content is not null)
        {
            return true;
        }

        if (ToolCallStreamingJsonHelper.TryExtractStringProperty(argumentsText, "content", out content)
            && !string.IsNullOrEmpty(content))
        {
            return true;
        }

        foreach (var line in argumentsText.Replace("\r\n", "\n").Split('\n'))
        {
            const string prefix = "content = ";
            var trimmed = line.Trim();
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                content = trimmed[prefix.Length..];
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reads a file_write body out of a stored tool result. The arguments header is
    /// <c>path=...</c> on the Arguments line and <c>content=</c> on the following lines, through
    /// the Summary line. A one-line <c>content = hello</c> form is accepted too.
    /// </summary>
    private static bool TryExtractPersistedFileWriteContent(string? toolContent, out string content)
    {
        content = string.Empty;
        if (string.IsNullOrWhiteSpace(toolContent))
        {
            return false;
        }

        var lines = toolContent.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var contentLine = -1;
        var first = string.Empty;
        var summaryLine = lines.Length;
        for (var i = 0; i < lines.Length; i++)
        {
            if (contentLine < 0 && TryTakeContentAssignment(lines[i], out first))
            {
                contentLine = i;
                continue;
            }

            if (contentLine >= 0 && lines[i].StartsWith("Summary:", StringComparison.OrdinalIgnoreCase))
            {
                summaryLine = i;
                break;
            }
        }

        if (contentLine < 0)
        {
            return false;
        }

        if (summaryLine == contentLine + 1)
        {
            content = first;
            return true;
        }

        var tail = string.Join("\n", lines[(contentLine + 1)..summaryLine]);
        content = string.IsNullOrEmpty(tail) ? first : first + "\n" + tail;
        return true;
    }

    private static bool TryTakeContentAssignment(string line, out string rest)
    {
        rest = string.Empty;
        var source = line.StartsWith("Arguments:", StringComparison.OrdinalIgnoreCase)
            ? line["Arguments:".Length..]
            : line;
        var index = source.IndexOf("content", StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            var after = source[(index + "content".Length)..].TrimStart();
            if (after.StartsWith('='))
            {
                rest = after[1..].TrimStart();
                return true;
            }

            index = source.IndexOf("content", index + 1, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool IsFileWriteContentPlaceholder(string content)
    {
        var trimmed = content.Trim();
        if (trimmed.Length < 8 || trimmed[0] != '(' || trimmed[^1] != ')')
        {
            return false;
        }

        var inner = trimmed[1..^1];
        var space = inner.IndexOf(' ');
        if (space <= 0 || !string.Equals(inner[space..], " chars", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        for (var i = 0; i < space; i++)
        {
            if (!char.IsDigit(inner[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryExtractPatchArgument(string? argumentsText, out string patch)
    {
        patch = string.Empty;
        if (string.IsNullOrWhiteSpace(argumentsText))
        {
            return false;
        }

        if (ToolCallStreamingJsonHelper.TryExtractStringProperty(argumentsText, "patch", out patch)
            && !string.IsNullOrWhiteSpace(patch))
        {
            return true;
        }

        foreach (var line in argumentsText.Replace("\r\n", "\n").Split('\n'))
        {
            const string prefix = "patch = ";
            var trimmed = line.Trim();
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                patch = trimmed[prefix.Length..];
                return !string.IsNullOrWhiteSpace(patch);
            }
        }

        return false;
    }

    private static bool LooksLikeUnifiedDiff(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return text.Contains("\n@@", StringComparison.Ordinal)
            || text.Contains("\r\n@@", StringComparison.Ordinal)
            || text.StartsWith("@@", StringComparison.Ordinal)
            || text.StartsWith("--- ", StringComparison.Ordinal)
            || text.StartsWith("diff --git", StringComparison.Ordinal);
    }
}
