using System.Collections.ObjectModel;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Streaming;

namespace Athlon.Agent.App.Services;

public sealed class SessionModifiedFilesTracker
{
    private readonly Dictionary<string, ModifiedFileViewModel> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _toolCallIdToName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _toolCallIdToArgs = new(StringComparer.Ordinal);

    public ObservableCollection<ModifiedFileViewModel> ModifiedFiles { get; } = new();

    public bool HasModifiedFiles => ModifiedFiles.Count > 0;

    public void Clear()
    {
        _byPath.Clear();
        _toolCallIdToName.Clear();
        _toolCallIdToArgs.Clear();
        ModifiedFiles.Clear();
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
            if (string.Equals(message.ToolName, "apply_patch", StringComparison.Ordinal)
                && status == ModifiedFileStatus.Succeeded)
            {
                foreach (var path in ModifiedFilePathExtractor.ExtractApplyPatchPaths(message.Content))
                {
                    Upsert(path, message.ToolName, status);
                }

                continue;
            }

            var relativePath = ModifiedFilePathExtractor.ExtractPathFromArguments(message.ToolArgumentsText);
            if (relativePath is not null)
            {
                Upsert(relativePath, message.ToolName, status);
            }
        }
    }

    private void HandleToolCallResult(string toolCallId, string content)
    {
        _toolCallIdToName.TryGetValue(toolCallId, out var toolName);
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
                    Upsert(path, toolName, status);
                }

                return;
            }
        }

        var relativePath = ModifiedFilePathExtractor.ExtractPathFromArguments(
            _toolCallIdToArgs.TryGetValue(toolCallId, out var args) ? args : null);
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
            Upsert(relativePath, toolName, status);
        }
    }

    private void Upsert(string relativePath, string toolName, ModifiedFileStatus status)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return;
        }

        var normalized = ToolPathNormalizer.ForModel(relativePath);
        if (_byPath.TryGetValue(normalized, out var existing))
        {
            existing.Status = status;
            existing.LastModifiedAt = DateTimeOffset.UtcNow;
            return;
        }

        var item = new ModifiedFileViewModel(normalized, toolName, status);
        _byPath[normalized] = item;
        ModifiedFiles.Add(item);
    }
}
