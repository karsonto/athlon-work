using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Athlon.Agent.App.ViewModels;

public sealed partial class ChatMessageViewModel : ObservableObject
{
    public ChatMessageViewModel(ChatMessage message, bool expandTool = false)
    {
        Role = message.Role.ToString();
        Content = message.Content;
        ReasoningContent = message.ReasoningContent ?? string.Empty;
        IsReasoningExpanded = true;
        CreatedAt = message.CreatedAt.ToLocalTime().ToString("HH:mm:ss");
        IsStreaming = false;
        IsUser = message.Role == MessageRole.User;
        IsTool = message.Role == MessageRole.Tool;
        IsCompaction = message.Role == MessageRole.Compaction;
        UserAttachmentSummary = message.ImageAttachments is { Count: > 0 }
            ? $"已附图片 {message.ImageAttachments.Count} 张"
            : string.Empty;
        IsHiddenPlaceholder = IsUser && (CompactionMessageContent.IsSummaryPlaceholder(message.Content)
            || SummaryMessageBuilder.IsSummaryMessage(message))
            || IsAssistantToolCallsOnly(message);
        DisplayRole = IsUser
            ? "您"
            : IsTool
                ? "工具"
                : IsCompaction
                    ? "上下文"
                    : "Athlon 助手";

        if (IsTool)
        {
            ParseToolContent(message.Content, out var toolCallId, out var toolName, out var header, out var summary, out var detail, out var argumentsText, out var status);
            ToolCallId = toolCallId;
            ToolName = toolName;
            ToolHeader = header;
            ToolSummary = summary;
            ToolDetail = detail;
            ToolArgumentsText = argumentsText;
            ToolCallStatus = status;
            IsToolRunning = false;
            IsExpanded = expandTool;
        }
        else if (IsCompaction)
        {
            ParseCompactionContent(message.Content, out var header, out var summary, out var detail);
            ToolCallId = null;
            ToolHeader = header;
            ToolSummary = summary;
            ToolDetail = detail;
            IsToolRunning = false;
            IsExpanded = expandTool;
        }
        else
        {
            ToolCallId = null;
            ToolHeader = string.Empty;
            ToolSummary = string.Empty;
            ToolDetail = string.Empty;
            ToolName = string.Empty;
            ToolArgumentsText = string.Empty;
            ToolCallStatus = ToolCallDisplayStatus.None;
            IsToolRunning = false;
        }
    }

    private ChatMessageViewModel(AgentToolCall toolCall)
    {
        Role = MessageRole.Tool.ToString();
        ToolCallId = toolCall.Id;
        ToolName = toolCall.Name;
        IsUser = false;
        IsTool = true;
        IsCompaction = false;
        IsHiddenPlaceholder = false;
        DisplayRole = "工具";
        IsToolRunning = true;
        ToolCallStatus = ToolCallDisplayStatus.Running;
        CreatedAt = DateTimeOffset.Now.ToLocalTime().ToString("HH:mm:ss");
        ToolHeader = $"Tool `{toolCall.Name}`";
        ToolArgumentsText = FormatArgumentsFull(toolCall.Arguments);
        ToolSummary = string.Empty;
        ToolDetail = string.Empty;
        Content = string.Empty;
        ReasoningContent = string.Empty;
        IsExpanded = false;
        IsStreaming = false;
        IsReasoningStreaming = false;
    }

    public string Role { get; private set; }

    [ObservableProperty]
    private string _content = string.Empty;

    [ObservableProperty]
    private string _reasoningContent = string.Empty;

    [ObservableProperty]
    private bool _isReasoningExpanded = true;

    [ObservableProperty]
    private bool _isReasoningStreaming;

    public bool HasReasoning => !string.IsNullOrWhiteSpace(ReasoningContent);

    public string ReasoningChevronGlyph => IsReasoningExpanded ? "▼" : "▶";

    [ObservableProperty]
    private string _createdAt = string.Empty;

    [ObservableProperty]
    private bool _isStreaming;

    public bool IsUser { get; }
    public bool IsTool { get; }
    public bool IsCompaction { get; }
    public string UserAttachmentSummary { get; }
    public bool IsCollapsibleCard => IsTool || IsCompaction;
    public bool IsHiddenPlaceholder { get; }
    public bool AssistantTone => !IsUser;
    public string CardTitle => IsCompaction ? "上下文压缩" : "工具调用";
    public string DisplayRole { get; }

    public string? ToolCallId { get; private set; }

    public int? StreamToolIndex { get; private set; }

    [ObservableProperty]
    private bool _isToolRunning;

    [ObservableProperty]
    private string _toolHeader = string.Empty;

    [ObservableProperty]
    private string _toolSummary = string.Empty;

    [ObservableProperty]
    private string _toolDetail = string.Empty;

    [ObservableProperty]
    private string _toolName = string.Empty;

    [ObservableProperty]
    private string _toolArgumentsText = string.Empty;

    [ObservableProperty]
    private ToolCallDisplayStatus _toolCallStatus;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isToolArgumentsStreaming;

    public bool HasToolArguments => !string.IsNullOrWhiteSpace(ToolArgumentsText)
        && ToolArgumentsText != "…";

    public bool ShowToolArgumentsPanel => IsToolArgumentsStreaming || HasToolArguments;

    public string ChevronGlyph => IsExpanded ? "▼" : "▶";

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ChevronGlyph));

    partial void OnToolArgumentsTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasToolArguments));
        OnPropertyChanged(nameof(ShowToolArgumentsPanel));
    }

    partial void OnIsToolArgumentsStreamingChanged(bool value) => OnPropertyChanged(nameof(ShowToolArgumentsPanel));

    partial void OnToolCallStatusChanged(ToolCallDisplayStatus value)
    {
        OnPropertyChanged(nameof(ToolStatusLabel));
        OnPropertyChanged(nameof(ShowToolStatusLabel));
        OnPropertyChanged(nameof(ShowToolArgumentsPanel));
    }

    public bool ShowToolStatusLabel => ToolCallStatus != ToolCallDisplayStatus.None;

    public string ToolStatusLabel => ToolCallStatus switch
    {
        ToolCallDisplayStatus.Preparing => "准备中…",
        ToolCallDisplayStatus.Running => "执行中…",
        ToolCallDisplayStatus.Succeeded => "成功",
        ToolCallDisplayStatus.Failed => "失败",
        ToolCallDisplayStatus.Cancelled => "已停止",
        _ => string.Empty
    };

    partial void OnReasoningContentChanged(string value)
    {
        OnPropertyChanged(nameof(HasReasoning));
    }

    partial void OnIsReasoningExpandedChanged(bool value) => OnPropertyChanged(nameof(ReasoningChevronGlyph));

    [RelayCommand]
    private void ToggleReasoningExpand() => IsReasoningExpanded = !IsReasoningExpanded;

    [RelayCommand]
    private void ToggleToolExpand() => IsExpanded = !IsExpanded;

    public static ChatMessageViewModel CreatePendingTool(AgentToolCall toolCall) => new(toolCall);

    public static ChatMessageViewModel CreateStreamingTool(int streamIndex) => new(streamIndex);

    public static ChatMessageViewModel CreateStreamingAssistant() =>
        new(ChatMessage.Create(MessageRole.Assistant, string.Empty))
        {
            IsStreaming = true
        };

    private ChatMessageViewModel(int streamIndex)
    {
        StreamToolIndex = streamIndex;
        Role = MessageRole.Tool.ToString();
        IsUser = false;
        IsTool = true;
        IsCompaction = false;
        IsHiddenPlaceholder = false;
        DisplayRole = "工具";
        ToolCallStatus = ToolCallDisplayStatus.Preparing;
        CreatedAt = DateTimeOffset.Now.ToLocalTime().ToString("HH:mm:ss");
        ToolHeader = "Tool …";
        ToolSummary = string.Empty;
        ToolDetail = string.Empty;
        ToolName = string.Empty;
        ToolArgumentsText = string.Empty;
        Content = string.Empty;
        ReasoningContent = string.Empty;
        IsExpanded = true;
        IsStreaming = false;
        IsReasoningStreaming = false;
        IsToolRunning = false;
    }

    public void UpdateStreamingToolCall(string? id, string? name, string argumentsJson)
    {
        if (!IsTool || ToolCallStatus != ToolCallDisplayStatus.Preparing)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(id))
        {
            ToolCallId = id;
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            ToolName = name;
            ToolHeader = $"Tool `{name}`";
        }

        IsToolArgumentsStreaming = true;
        ToolArgumentsText = string.IsNullOrEmpty(argumentsJson) ? "…" : argumentsJson;
    }

    public void PromoteStreamingToolToRunning(AgentToolCall toolCall)
    {
        if (!IsTool)
        {
            return;
        }

        StreamToolIndex = null;
        ToolCallId = toolCall.Id;
        ToolName = toolCall.Name;
        ToolHeader = $"Tool `{toolCall.Name}`";
        ToolArgumentsText = FormatArgumentsFull(toolCall.Arguments);
        IsToolArgumentsStreaming = false;
        ToolCallStatus = ToolCallDisplayStatus.Running;
        IsToolRunning = true;
    }

    public void MarkStreamingToolCancelled()
    {
        if (!IsTool || ToolCallStatus != ToolCallDisplayStatus.Preparing)
        {
            return;
        }

        StreamToolIndex = null;
        IsToolArgumentsStreaming = false;
        ToolCallStatus = ToolCallDisplayStatus.Cancelled;
        ToolSummary = "已停止";
    }

    public void AppendStreamingToken(string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return;
        }

        Content += token;
    }

    public void AppendStreamingReasoningToken(string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return;
        }

        IsReasoningStreaming = true;
        ReasoningContent += token;
    }

    public void CompleteStreamingAssistant(ChatMessage message)
    {
        Content = message.Content;
        ReasoningContent = message.ReasoningContent ?? ReasoningContent;
        CreatedAt = message.CreatedAt.ToLocalTime().ToString("HH:mm:ss");
        IsStreaming = false;
        IsReasoningStreaming = false;
    }

    public void MarkStreamingCancelled()
    {
        if (!IsStreaming)
        {
            return;
        }

        IsStreaming = false;
        IsReasoningStreaming = false;
        if (string.IsNullOrWhiteSpace(Content))
        {
            Content = "（已停止）";
        }
    }

    public void ApplyCompletedTool(ChatMessage message)
    {
        if (!IsTool)
        {
            return;
        }

        Content = message.Content;
        CreatedAt = message.CreatedAt.ToLocalTime().ToString("HH:mm:ss");
        IsStreaming = false;
        ParseToolContent(message.Content, out var toolCallId, out var toolName, out var header, out var summary, out var detail, out var argumentsText, out var status);
        if (!string.IsNullOrWhiteSpace(toolCallId))
        {
            ToolCallId = toolCallId;
        }

        if (!string.IsNullOrWhiteSpace(toolName))
        {
            ToolName = toolName;
        }

        ToolHeader = header;
        ToolSummary = summary;
        ToolDetail = detail;
        if (!string.IsNullOrWhiteSpace(argumentsText))
        {
            ToolArgumentsText = argumentsText;
        }

        ToolCallStatus = status;
        IsToolRunning = false;
    }

    public void MarkToolCancelled()
    {
        if (!IsTool || !IsToolRunning)
        {
            return;
        }

        IsToolRunning = false;
        ToolCallStatus = ToolCallDisplayStatus.Cancelled;
        ToolSummary = "已停止";
    }

    private static void ParseToolContent(
        string content,
        out string? toolCallId,
        out string toolName,
        out string header,
        out string summary,
        out string detail,
        out string argumentsText,
        out ToolCallDisplayStatus status)
    {
        toolCallId = null;
        toolName = string.Empty;
        argumentsText = string.Empty;
        status = ToolCallDisplayStatus.Succeeded;
        var lines = content.Replace("\r\n", "\n").Split('\n');
        header = "工具调用";
        summary = string.Empty;

        foreach (var line in lines)
        {
            if (line.StartsWith("ToolCallId:", StringComparison.OrdinalIgnoreCase))
            {
                toolCallId = line["ToolCallId:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("Arguments:", StringComparison.OrdinalIgnoreCase))
            {
                argumentsText = FormatArgumentsFromPersistedLine(line["Arguments:".Length..].Trim());
                continue;
            }

            if (!string.IsNullOrWhiteSpace(line) && header == "工具调用" && line.StartsWith("Tool `", StringComparison.Ordinal))
            {
                header = line.Trim();
                toolName = TryParseToolName(header);
                status = ParseToolStatus(header);
                continue;
            }

            if (line.StartsWith("Summary:", StringComparison.OrdinalIgnoreCase))
            {
                summary = line["Summary:".Length..].Trim();
            }
        }

        detail = content.Trim();
    }

    private static string TryParseToolName(string header)
    {
        const string prefix = "Tool `";
        var start = header.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }

        start += prefix.Length;
        var end = header.IndexOf('`', start);
        return end > start ? header[start..end] : string.Empty;
    }

    private static ToolCallDisplayStatus ParseToolStatus(string header)
    {
        if (header.Contains("failed", StringComparison.OrdinalIgnoreCase))
        {
            return ToolCallDisplayStatus.Failed;
        }

        if (header.Contains("succeeded", StringComparison.OrdinalIgnoreCase))
        {
            return ToolCallDisplayStatus.Succeeded;
        }

        return ToolCallDisplayStatus.Succeeded;
    }

    private static void ParseCompactionContent(string content, out string header, out string summary, out string detail)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n');
        header = "上下文压缩";
        summary = string.Empty;
        var kind = string.Empty;

        foreach (var line in lines)
        {
            if (line.StartsWith("CompactionKind:", StringComparison.OrdinalIgnoreCase))
            {
                kind = line["CompactionKind:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("Summary:", StringComparison.OrdinalIgnoreCase))
            {
                summary = line["Summary:".Length..].Trim();
            }
        }

        header = kind switch
        {
            "microcompact" => "微压缩 · 清理较早工具输出",
            "autocompact" => "自动压缩 · 对话摘要",
            "conversationcompact" => "对话压缩 · 摘要 + 保留尾部",
            "manualcompact" => "手动压缩 · 对话摘要",
            _ => header
        };

        detail = content.Trim();
    }

    public static bool IsAssistantToolCallsOnly(ChatMessage message) =>
        message.Role == MessageRole.Assistant
        && !string.IsNullOrWhiteSpace(message.ToolCallsJson)
        && string.IsNullOrWhiteSpace(message.Content)
        && string.IsNullOrWhiteSpace(message.ReasoningContent);

    private static string FormatArgumentsFull(IReadOnlyDictionary<string, string> arguments) =>
        arguments.Count == 0
            ? "(无参数)"
            : string.Join(
                Environment.NewLine,
                arguments.Select(argument =>
                {
                    var value = string.Equals(argument.Key, ToolPathNormalizer.PathArgumentName, StringComparison.OrdinalIgnoreCase)
                        ? ToolPathNormalizer.ForModel(argument.Value)
                        : argument.Value;
                    return $"{argument.Key} = {value}";
                }));

    private static string FormatArgumentsFromPersistedLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || string.Equals(line, "(none)", StringComparison.OrdinalIgnoreCase))
        {
            return "(无参数)";
        }

        if (!line.Contains(';'))
        {
            return line.Replace("=", " = ", StringComparison.Ordinal);
        }

        return string.Join(
            Environment.NewLine,
            line.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(part =>
                {
                    var separator = part.IndexOf('=');
                    return separator < 0 ? part : $"{part[..separator].Trim()} = {part[(separator + 1)..].Trim()}";
                }));
    }
}
