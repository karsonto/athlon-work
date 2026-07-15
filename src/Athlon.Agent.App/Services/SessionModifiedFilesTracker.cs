using System.Collections.ObjectModel;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Streaming;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.App.Services;

public sealed class SessionModifiedFilesTracker
{
    private readonly Dictionary<string, ModifiedFileViewModel> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _toolCallIdToName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _toolCallIdToArgs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _currentTurnPaths = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<ModifiedFileViewModel> ModifiedFiles { get; } = new();

    public bool HasModifiedFiles => ModifiedFiles.Count > 0;

    public void Clear()
    {
        _byPath.Clear();
        _toolCallIdToName.Clear();
        _toolCallIdToArgs.Clear();
        _currentTurnPaths.Clear();
        ModifiedFiles.Clear();
    }

    public void BeginTurn() => _currentTurnPaths.Clear();

    public IReadOnlyList<ModifiedFileViewModel> TakeCurrentTurnSucceededFiles()
    {
        if (_currentTurnPaths.Count == 0)
        {
            return Array.Empty<ModifiedFileViewModel>();
        }

        return ModifiedFiles
            .Where(file =>
                _currentTurnPaths.Contains(file.RelativePath)
                && file.Status == ModifiedFileStatus.Succeeded)
            .ToList();
    }

    /// <summary>Takes succeeded files for the current segment and clears them so the next bubble is independent.</summary>
    public IReadOnlyList<ModifiedFileViewModel> TakeAndClearSegmentSucceededFiles()
    {
        var files = TakeCurrentTurnSucceededFiles();
        foreach (var file in files)
        {
            _currentTurnPaths.Remove(file.RelativePath);
        }

        return files;
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
                        Upsert(path, argsToolName, ModifiedFileStatus.Pending);
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
                        Upsert(path, endToolName, ModifiedFileStatus.Pending);
                    }
                }

                break;
            case AgentStreamEvent.ToolCallResult(var toolCallId, var content, _):
                HandleToolCallResult(toolCallId, content);
                _toolCallIdToName.Remove(toolCallId);
                _toolCallIdToArgs.Remove(toolCallId);
                break;
        }
    }

    public void RebuildFromMessages(IReadOnlyList<ChatMessageViewModel> messages)
    {
        Clear();
        foreach (var message in messages)
        {
            if (!message.IsTool || !ModifiedFilePathExtractor.IsFileTool(message.ToolName))
            {
                continue;
            }

            var status = ModifiedFilePathExtractor.ToModifiedFileStatus(message.ToolCallStatus);
            var argsText = message.ToolArgumentsText;
            if (string.Equals(message.ToolName, "apply_patch", StringComparison.Ordinal)
                && status == ModifiedFileStatus.Succeeded)
            {
                foreach (var path in ModifiedFilePathExtractor.ExtractApplyPatchPaths(message.Content))
                {
                    var item = Upsert(path, message.ToolName, status);
                    TryAttachDiff(item, message.ToolName, argsText, message.Content);
                }

                continue;
            }

            var relativePath = ModifiedFilePathExtractor.ExtractPathFromArguments(argsText);
            if (relativePath is null)
            {
                continue;
            }

            var file = Upsert(relativePath, message.ToolName, status);
            if (status == ModifiedFileStatus.Succeeded)
            {
                TryAttachDiff(file, message.ToolName, argsText, message.Content);
            }
        }
    }

    /// <summary>Builds per-turn file-change groups from display messages (for WebChat replay).</summary>
    public static IReadOnlyList<IReadOnlyList<ModifiedFileViewModel>> BuildTurnFileGroups(
        IReadOnlyList<ChatMessageViewModel> messages)
    {
        var groups = new List<IReadOnlyList<ModifiedFileViewModel>>();
        var current = new List<ModifiedFileViewModel>();
        var byPath = new Dictionary<string, ModifiedFileViewModel>(StringComparer.OrdinalIgnoreCase);

        void Flush()
        {
            if (current.Count > 0)
            {
                groups.Add(current.ToList());
                current.Clear();
                byPath.Clear();
            }
        }

        foreach (var message in messages)
        {
            if (message.IsUser)
            {
                Flush();
                continue;
            }

            if (!message.IsTool || !ModifiedFilePathExtractor.IsFileTool(message.ToolName))
            {
                continue;
            }

            var status = ModifiedFilePathExtractor.ToModifiedFileStatus(message.ToolCallStatus);
            if (status != ModifiedFileStatus.Succeeded)
            {
                continue;
            }

            void AddOrUpdate(string path)
            {
                var normalized = ToolPathNormalizer.ForModel(path);
                if (byPath.TryGetValue(normalized, out var existing))
                {
                    TryAttachDiff(existing, message.ToolName, message.ToolArgumentsText, message.Content);
                    return;
                }

                var item = new ModifiedFileViewModel(normalized, message.ToolName, status);
                TryAttachDiff(item, message.ToolName, message.ToolArgumentsText, message.Content);
                byPath[normalized] = item;
                current.Add(item);
            }

            if (string.Equals(message.ToolName, "apply_patch", StringComparison.Ordinal))
            {
                foreach (var path in ModifiedFilePathExtractor.ExtractApplyPatchPaths(message.Content))
                {
                    AddOrUpdate(path);
                }

                continue;
            }

            var relativePath = ModifiedFilePathExtractor.ExtractPathFromArguments(message.ToolArgumentsText);
            if (relativePath is not null)
            {
                AddOrUpdate(relativePath);
            }
        }

        Flush();
        return groups;
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
        if (string.Equals(toolName, "apply_patch", StringComparison.Ordinal))
        {
            var paths = ModifiedFilePathExtractor.ExtractApplyPatchPaths(content);
            if (paths.Count > 0)
            {
                foreach (var path in paths)
                {
                    var item = Upsert(path, toolName, status);
                    if (status == ModifiedFileStatus.Succeeded)
                    {
                        TryAttachDiff(item, toolName, args, content);
                    }
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
            var item = Upsert(relativePath, toolName, status);
            if (status == ModifiedFileStatus.Succeeded)
            {
                TryAttachDiff(item, toolName, args, content);
            }
        }
    }

    private ModifiedFileViewModel Upsert(string relativePath, string toolName, ModifiedFileStatus status)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("Path is required.", nameof(relativePath));
        }

        var normalized = ToolPathNormalizer.ForModel(relativePath);
        _currentTurnPaths.Add(normalized);

        if (_byPath.TryGetValue(normalized, out var existing))
        {
            existing.SetToolName(toolName);
            existing.Status = status;
            existing.LastModifiedAt = DateTimeOffset.UtcNow;
            return existing;
        }

        var item = new ModifiedFileViewModel(normalized, toolName, status);
        _byPath[normalized] = item;
        ModifiedFiles.Add(item);
        return item;
    }

    private static void TryAttachDiff(
        ModifiedFileViewModel file,
        string? toolName,
        string? argumentsText,
        string? toolContent)
    {
        var diff = ExtractDiff(toolName, argumentsText, toolContent, file.RelativePath);
        if (!string.IsNullOrWhiteSpace(diff))
        {
            file.SetDiff(diff);
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
            if (TryExtractFileWriteContent(argumentsText, out var content))
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
