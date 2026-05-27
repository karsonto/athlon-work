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
            ParseToolContent(message.Content, out var toolCallId, out var header, out var summary, out var detail);
            ToolCallId = toolCallId;
            ToolHeader = header;
            ToolSummary = summary;
            ToolDetail = detail;
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
            IsToolRunning = false;
        }
    }

    private ChatMessageViewModel(AgentToolCall toolCall)
    {
        Role = MessageRole.Tool.ToString();
        ToolCallId = toolCall.Id;
        IsUser = false;
        IsTool = true;
        IsCompaction = false;
        IsHiddenPlaceholder = false;
        DisplayRole = "工具";
        IsToolRunning = true;
        CreatedAt = DateTimeOffset.Now.ToLocalTime().ToString("HH:mm:ss");
        ToolHeader = $"Tool `{toolCall.Name}` running...";
        ToolSummary = FormatArgumentsPreview(toolCall.Arguments);
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
    public bool IsCollapsibleCard => IsTool || IsCompaction;
    public bool IsHiddenPlaceholder { get; }
    public bool AssistantTone => !IsUser;
    public string CardTitle => IsCompaction ? "上下文压缩" : "工具调用";
    public string DisplayRole { get; }

    public string? ToolCallId { get; private set; }

    [ObservableProperty]
    private bool _isToolRunning;

    [ObservableProperty]
    private string _toolHeader = string.Empty;

    [ObservableProperty]
    private string _toolSummary = string.Empty;

    [ObservableProperty]
    private string _toolDetail = string.Empty;

    [ObservableProperty]
    private bool _isExpanded;

    public string ChevronGlyph => IsExpanded ? "▼" : "▶";

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ChevronGlyph));

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

    public static ChatMessageViewModel CreateStreamingAssistant() =>
        new(ChatMessage.Create(MessageRole.Assistant, string.Empty))
        {
            IsStreaming = true
        };

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
        ParseToolContent(message.Content, out var toolCallId, out var header, out var summary, out var detail);
        if (!string.IsNullOrWhiteSpace(toolCallId))
        {
            ToolCallId = toolCallId;
        }

        ToolHeader = header;
        ToolSummary = summary;
        ToolDetail = detail;
        IsToolRunning = false;
    }

    public void MarkToolCancelled()
    {
        if (!IsTool || !IsToolRunning)
        {
            return;
        }

        IsToolRunning = false;
        ToolSummary = "已停止";
    }

    private static void ParseToolContent(string content, out string? toolCallId, out string header, out string summary, out string detail)
    {
        toolCallId = null;
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

            if (!string.IsNullOrWhiteSpace(line) && header == "工具调用")
            {
                header = line.Trim();
            }

            if (line.StartsWith("Summary:", StringComparison.OrdinalIgnoreCase))
            {
                summary = line["Summary:".Length..].Trim();
            }
        }

        detail = content.Trim();
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

    private static string FormatArgumentsPreview(IReadOnlyDictionary<string, string> arguments) =>
        arguments.Count == 0
            ? string.Empty
            : string.Join("; ", arguments.Select(argument => $"{argument.Key}={argument.Value}"));
}
