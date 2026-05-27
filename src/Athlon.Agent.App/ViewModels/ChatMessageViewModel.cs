using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Skills;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace Athlon.Agent.App.ViewModels;

public sealed partial class ChatMessageViewModel : ObservableObject
{
    public ChatMessageViewModel(ChatMessage message, bool expandTool = false)
    {
        Role = message.Role.ToString();
        Content = message.Content;
        CreatedAt = message.CreatedAt.ToLocalTime().ToString("HH:mm:ss");
        IsStreaming = false;
        IsUser = message.Role == MessageRole.User;
        IsTool = message.Role == MessageRole.Tool;
        DisplayRole = IsUser ? "您" : IsTool ? "工具" : "Athlon 助手";

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
        DisplayRole = "工具";
        IsToolRunning = true;
        CreatedAt = DateTimeOffset.Now.ToLocalTime().ToString("HH:mm:ss");
        ToolHeader = $"Tool `{toolCall.Name}` running...";
        ToolSummary = FormatArgumentsPreview(toolCall.Arguments);
        ToolDetail = string.Empty;
        Content = string.Empty;
        IsExpanded = false;
        IsStreaming = false;
    }

    public string Role { get; private set; }

    [ObservableProperty]
    private string _content = string.Empty;

    [ObservableProperty]
    private string _createdAt = string.Empty;

    [ObservableProperty]
    private bool _isStreaming;
    public bool IsUser { get; }
    public bool IsTool { get; }
    public bool AssistantTone => !IsUser;
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

    public void CompleteStreamingAssistant(ChatMessage message)
    {
        Content = message.Content;
        CreatedAt = message.CreatedAt.ToLocalTime().ToString("HH:mm:ss");
        IsStreaming = false;
    }

    public void MarkStreamingCancelled()
    {
        if (!IsStreaming)
        {
            return;
        }

        IsStreaming = false;
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

    private static string FormatArgumentsPreview(IReadOnlyDictionary<string, string> arguments) =>
        arguments.Count == 0
            ? string.Empty
            : string.Join("; ", arguments.Select(argument => $"{argument.Key}={argument.Value}"));
}
