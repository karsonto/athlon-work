using System.Text;
using Athlon.Agent.App.Resources;
using Athlon.Agent.App.Services;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.SubAgents;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Athlon.Agent.App.ViewModels;

public sealed partial class ChatMessageViewModel : ObservableObject
{
    public const string PendingManualCompactionMessageId = "pending-manual-compaction";

    public const int MaxToolDetailDisplayChars = 16_384;
    private const int ToolDetailPreviewChars = 4_096;

    private string _toolDetailFull = string.Empty;
    private StringBuilder? _streamingContentBuilder;
    private StringBuilder? _streamingReasoningBuilder;
    private int _publishedStreamingContentLength;
    private int _publishedStreamingReasoningLength;

    private readonly bool _isFoldedHistoryPlaceholder;

    public ChatMessageViewModel(ChatMessage message, bool expandTool = false, bool isFoldedHistoryPlaceholder = false)
    {
        _isFoldedHistoryPlaceholder = isFoldedHistoryPlaceholder;
        MessageId = message.Id;
        Role = message.Role.ToString();
        Content = message.Content;
        ReasoningContent = message.ReasoningContent ?? string.Empty;
        IsReasoningExpanded = true;
        CreatedAt = AppTimeZone.ToChina(message.CreatedAt).ToString("HH:mm:ss");
        IsStreaming = false;
        IsUser = message.Role == MessageRole.User;
        IsTool = message.Role == MessageRole.Tool;
        IsCompaction = message.Role == MessageRole.Compaction;
        UserAttachmentSummary = message.ImageAttachments is { Count: > 0 }
            ? Strings.Format("Chat_ImageAttachmentCount", message.ImageAttachments.Count)
            : string.Empty;
        ImageAttachments = message.ImageAttachments is { Count: > 0 }
            ? message.ImageAttachments
            : Array.Empty<ImageAttachment>();
        IsHiddenPlaceholder = isFoldedHistoryPlaceholder
            ? false
            : IsUser && (CompactionMessageContent.IsSummaryPlaceholder(message.Content)
            || SummaryMessageBuilder.IsSummaryMessage(message)
            || SubAgentAutoContinuePrompt.IsAutoContinueMessage(message))
            || IsAssistantToolCallsOnly(message);
        DisplayRole = IsUser
            ? Strings.Get("Chat_RoleUser")
            : IsTool
                ? Strings.Get("Chat_RoleTool")
                : IsCompaction
                    ? Strings.Get("Chat_RoleContext")
                    : Strings.Get("Chat_RoleAssistant");

        if (IsTool)
        {
            ToolMessageDisplayParser.ParseToolContent(message.Content, out var toolCallId, out var toolName, out var header, out var summary, out var detail, out var argumentsText, out var status);
            ToolCallId = toolCallId;
            ToolName = toolName;
            ToolHeader = header;
            ToolSummary = summary;
            AssignToolDetail(detail);
            ToolArgumentsText = argumentsText;
            ToolCallStatus = status;
            IsToolRunning = false;
            IsExpanded = expandTool;
        }
        else if (IsCompaction)
        {
            var display = CompactionAuditDisplay.Parse(message.Content);
            ToolCallId = null;
            CompactionCardTitle = display.CardTitle;
            ToolHeader = display.StrategySubtitle;
            ToolSummary = AppendCompactionDisplayNotice(display.Summary);
            AssignToolDetail(display.Detail);
            IsToolRunning = false;
            IsExpanded = expandTool;
        }
        else
        {
            ToolCallId = null;
            ToolHeader = string.Empty;
            ToolSummary = string.Empty;
            ToolDetail = string.Empty;
            ToolDetailDisplay = string.Empty;
            ToolName = string.Empty;
            ToolArgumentsText = string.Empty;
            ToolCallStatus = ToolCallDisplayStatus.None;
            IsToolRunning = false;
        }
    }

    private ChatMessageViewModel(string pendingCompactionId)
    {
        MessageId = pendingCompactionId;
        ToolCallId = pendingCompactionId;
        Role = MessageRole.Compaction.ToString();
        IsUser = false;
        IsTool = false;
        IsCompaction = true;
        IsHiddenPlaceholder = false;
        DisplayRole = Strings.Get("Chat_RoleContext");
        CompactionCardTitle = CompactionAuditDisplay.GetCardTitle(CompactionStrategy.ManualCompact);
        ToolHeader = Strings.Get("Chat_CompactionManualLayersSubtitle");
        ToolSummary = Strings.Get("Chat_CompactionRunning");
        ToolDetail = string.Empty;
        ToolDetailDisplay = string.Empty;
        ToolName = string.Empty;
        ToolArgumentsText = string.Empty;
        Content = string.Empty;
        ReasoningContent = string.Empty;
        UserAttachmentSummary = string.Empty;
        ImageAttachments = Array.Empty<ImageAttachment>();
        CreatedAt = AppTimeZone.Now.ToString("HH:mm:ss");
        IsToolRunning = true;
        ToolCallStatus = ToolCallDisplayStatus.Running;
        IsExpanded = true;
        IsStreaming = false;
        IsReasoningStreaming = false;
    }

    private ChatMessageViewModel(AgentToolCall toolCall)
    {
        MessageId = $"pending-tool-{toolCall.Id}";
        Role = MessageRole.Tool.ToString();
        ToolCallId = toolCall.Id;
        ToolName = toolCall.Name;
        IsUser = false;
        IsTool = true;
        IsCompaction = false;
        IsHiddenPlaceholder = false;
        DisplayRole = Strings.Get("Chat_RoleTool");
        IsToolRunning = true;
        ToolCallStatus = ToolCallDisplayStatus.Running;
        CreatedAt = AppTimeZone.Now.ToString("HH:mm:ss");
        ToolHeader = $"Tool `{toolCall.Name}`";
        ToolArgumentsText = ToolMessageDisplayParser.FormatArgumentsFull(toolCall.Arguments, toolCall.Name);
        ToolSummary = string.Empty;
        ToolDetail = string.Empty;
        ToolDetailDisplay = string.Empty;
        Content = string.Empty;
        ReasoningContent = string.Empty;
        UserAttachmentSummary = string.Empty;
        ImageAttachments = Array.Empty<ImageAttachment>();
        IsExpanded = false;
        IsStreaming = false;
        IsReasoningStreaming = false;
    }

    public string MessageId { get; }

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
    public IReadOnlyList<ImageAttachment> ImageAttachments { get; }
    public bool IsCollapsibleCard => IsTool || IsCompaction || _isFoldedHistoryPlaceholder;
    public bool IsHiddenPlaceholder { get; }
    public bool AssistantTone => !IsUser;
    public string CompactionCardTitle { get; } = string.Empty;
    public string CardTitle => IsCompaction
        ? (string.IsNullOrWhiteSpace(CompactionCardTitle) ? Strings.Get("Chat_CompactionDefault") : CompactionCardTitle)
        : Strings.Get("Chat_ToolCallTitle");
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
    private string _toolDetailDisplay = string.Empty;

    [ObservableProperty]
    private string _toolName = string.Empty;

    [ObservableProperty]
    private string _toolArgumentsText = string.Empty;

    [ObservableProperty]
    private ToolCallDisplayStatus _toolCallStatus;

    [ObservableProperty]
    private ToolApprovalState _toolApprovalState;

    [ObservableProperty]
    private string _toolApprovalArgumentsPreview = string.Empty;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isToolArgumentsStreaming;

    public bool HasToolArguments => !string.IsNullOrWhiteSpace(ToolArgumentsText)
        && ToolArgumentsText != "…";

    public bool ShowToolArgumentsPanel => IsToolArgumentsStreaming || HasToolArguments;

    public string ChevronGlyph => IsExpanded ? "▼" : "▶";

    public string ToolDetailExpandedDisplay =>
        TruncateToolDetailForDisplay(_toolDetailFull, MaxToolDetailDisplayChars);

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(ChevronGlyph));
        RefreshToolDetailDisplay();
    }

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

    partial void OnToolApprovalStateChanged(ToolApprovalState value)
    {
        OnPropertyChanged(nameof(ToolStatusLabel));
        OnPropertyChanged(nameof(ShowToolStatusLabel));
    }

    public bool ShowToolStatusLabel =>
        ToolCallStatus != ToolCallDisplayStatus.None
        || ToolApprovalState != ToolApprovalState.None;

    public string ToolStatusLabel => ToolApprovalState switch
    {
        ToolApprovalState.Pending => Strings.Get("Chat_ToolApprovalPending"),
        ToolApprovalState.Approved => Strings.Get("Chat_ToolApprovalAllowedStatus"),
        ToolApprovalState.Denied => Strings.Get("Chat_ToolApprovalDeniedStatus"),
        _ => ToolCallStatus switch
        {
            ToolCallDisplayStatus.Preparing => Strings.Get("Chat_ToolStatusPreparing"),
            ToolCallDisplayStatus.Running => Strings.Get("Chat_ToolStatusRunning"),
            ToolCallDisplayStatus.AwaitingApproval => Strings.Get("Chat_ToolApprovalPending"),
            ToolCallDisplayStatus.ApprovalDenied => Strings.Get("Chat_ToolApprovalDeniedStatus"),
            ToolCallDisplayStatus.Succeeded => Strings.Get("Chat_ToolStatusSucceeded"),
            ToolCallDisplayStatus.Failed => Strings.Get("Chat_ToolStatusFailed"),
            ToolCallDisplayStatus.Cancelled => Strings.Get("Chat_ToolStatusCancelled"),
            _ => string.Empty
        }
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

    public static ChatMessageViewModel CreatePendingManualCompaction() =>
        new(PendingManualCompactionMessageId);

    public static ChatMessageViewModel CreateStreamingTool(int streamIndex) => new(streamIndex);

    public static ChatMessageViewModel CreateStreamingAssistant(string? messageId = null) =>
        new(ChatMessage.CreateWithId(messageId ?? Guid.NewGuid().ToString("N"), MessageRole.Assistant, string.Empty))
        {
            IsStreaming = true
        };

    private ChatMessageViewModel(int streamIndex)
    {
        MessageId = $"stream-tool-{streamIndex}";
        StreamToolIndex = streamIndex;
        Role = MessageRole.Tool.ToString();
        IsUser = false;
        IsTool = true;
        IsCompaction = false;
        IsHiddenPlaceholder = false;
        DisplayRole = Strings.Get("Chat_RoleTool");
        ToolCallStatus = ToolCallDisplayStatus.Preparing;
        CreatedAt = AppTimeZone.Now.ToString("HH:mm:ss");
        ToolHeader = "Tool …";
        ToolSummary = string.Empty;
        ToolDetail = string.Empty;
        ToolDetailDisplay = string.Empty;
        ToolName = string.Empty;
        ToolArgumentsText = string.Empty;
        Content = string.Empty;
        ReasoningContent = string.Empty;
        UserAttachmentSummary = string.Empty;
        ImageAttachments = Array.Empty<ImageAttachment>();
        IsExpanded = false;
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
        ToolArgumentsText = FileWriteToolArgumentsDisplay.IsFileWrite(toolCall.Name)
            ? FileWriteToolArgumentsDisplay.FormatArgumentsForPersistedDisplay(toolCall.Arguments)
            : ToolMessageDisplayParser.FormatArgumentsFull(toolCall.Arguments, toolCall.Name);
        IsToolArgumentsStreaming = false;
        ToolCallStatus = ToolCallDisplayStatus.Running;
        IsToolRunning = true;
    }

    public void PromoteStreamingToolToRunningDisplay(string toolCallId, string toolName, string argumentsDisplayText)
    {
        if (!IsTool)
        {
            return;
        }

        StreamToolIndex = null;
        ToolCallId = toolCallId;
        ToolName = toolName;
        ToolHeader = $"Tool `{toolName}`";
        ToolArgumentsText = string.IsNullOrWhiteSpace(argumentsDisplayText) ? "…" : argumentsDisplayText;
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
        ToolSummary = Strings.Get("Chat_ToolStopped");
    }

    public void MarkAwaitingApproval(string argumentsPreview)
    {
        if (!IsTool)
        {
            return;
        }

        ToolApprovalState = ToolApprovalState.Pending;
        ToolApprovalArgumentsPreview = argumentsPreview;
        if (!string.IsNullOrWhiteSpace(argumentsPreview))
        {
            ToolArgumentsText = argumentsPreview;
        }

        ToolCallStatus = ToolCallDisplayStatus.AwaitingApproval;
        IsToolRunning = true;
        IsExpanded = true;
        IsToolArgumentsStreaming = false;
    }

    public void ApplyToolApprovalDecision(ToolApprovalDecision decision)
    {
        if (ToolApprovalState != ToolApprovalState.Pending)
        {
            return;
        }

        if (decision == ToolApprovalDecision.Approved)
        {
            ToolApprovalState = ToolApprovalState.Approved;
            ToolCallStatus = ToolCallDisplayStatus.Running;
            IsToolRunning = true;
            return;
        }

        ToolApprovalState = ToolApprovalState.Denied;
        ToolCallStatus = ToolCallDisplayStatus.ApprovalDenied;
        IsToolRunning = false;
        ToolSummary = Strings.Get("Chat_ToolApprovalDenied");
    }

    public void AppendStreamingToken(string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return;
        }

        if (_streamingContentBuilder is null)
        {
            _streamingContentBuilder = new StringBuilder(Content);
            _publishedStreamingContentLength = Content.Length;
        }

        _streamingContentBuilder.Append(token);
    }

    public void AppendStreamingReasoningToken(string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return;
        }

        IsReasoningStreaming = true;
        if (_streamingReasoningBuilder is null)
        {
            _streamingReasoningBuilder = new StringBuilder(ReasoningContent);
            _publishedStreamingReasoningLength = ReasoningContent.Length;
        }

        _streamingReasoningBuilder.Append(token);
    }

    public void FlushStreamingContent()
    {
        if (_streamingContentBuilder is { } contentBuilder
            && contentBuilder.Length != _publishedStreamingContentLength)
        {
            Content = contentBuilder.ToString();
            _publishedStreamingContentLength = contentBuilder.Length;
        }

        if (_streamingReasoningBuilder is { } reasoningBuilder
            && reasoningBuilder.Length != _publishedStreamingReasoningLength)
        {
            ReasoningContent = reasoningBuilder.ToString();
            _publishedStreamingReasoningLength = reasoningBuilder.Length;
        }
    }

    public bool HasBufferedStreamingContent() =>
        _streamingContentBuilder is { } contentBuilder
            && contentBuilder.Length != _publishedStreamingContentLength
        || _streamingReasoningBuilder is { } reasoningBuilder
            && reasoningBuilder.Length != _publishedStreamingReasoningLength;

    public void CompleteStreamingAssistant(ChatMessage message)
    {
        FlushStreamingContent();
        _streamingContentBuilder = null;
        _streamingReasoningBuilder = null;
        Content = message.Content;
        ReasoningContent = message.ReasoningContent ?? ReasoningContent;
        CreatedAt = AppTimeZone.ToChina(message.CreatedAt).ToString("HH:mm:ss");
        IsStreaming = false;
        IsReasoningStreaming = false;
    }

    public void SealStreamingDisplay()
    {
        FlushStreamingContent();
        _streamingContentBuilder = null;
        _streamingReasoningBuilder = null;
        IsStreaming = false;
        IsReasoningStreaming = false;
    }

    public void MarkStreamingCancelled()
    {
        if (!IsStreaming)
        {
            return;
        }

        FlushStreamingContent();
        _streamingContentBuilder = null;
        _streamingReasoningBuilder = null;
        IsStreaming = false;
        IsReasoningStreaming = false;
        if (string.IsNullOrWhiteSpace(Content))
        {
            Content = Strings.Get("Chat_ToolStoppedContent");
        }
    }

    public void ApplyCompletedTool(ChatMessage message)
    {
        if (!IsTool)
        {
            return;
        }

        Content = message.Content;
        CreatedAt = AppTimeZone.ToChina(message.CreatedAt).ToString("HH:mm:ss");
        IsStreaming = false;
        ToolMessageDisplayParser.ParseToolContent(message.Content, out var toolCallId, out var toolName, out var header, out var summary, out var detail, out var argumentsText, out var status);
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
        AssignToolDetail(detail);
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
        ToolSummary = Strings.Get("Chat_ToolStopped");
    }

    public void MarkCompactionCancelled()
    {
        if (!IsCompaction || !IsToolRunning)
        {
            return;
        }

        IsToolRunning = false;
        ToolCallStatus = ToolCallDisplayStatus.Cancelled;
        ToolSummary = Strings.Get("Chat_CompactionCancelled");
    }

    public void ApplyCompletedCompaction(ChatMessage auditMessage)
    {
        if (!IsCompaction || auditMessage.Role != MessageRole.Compaction)
        {
            return;
        }

        var display = CompactionAuditDisplay.Parse(auditMessage.Content);
        ToolHeader = display.StrategySubtitle;
        ToolSummary = AppendCompactionDisplayNotice(display.Summary);
        AssignToolDetail(display.Detail);
        IsToolRunning = false;
        ToolCallStatus = ToolCallDisplayStatus.Succeeded;
        IsExpanded = false;
    }

    /// <summary>Appends incremental stdout/stderr output while the tool is still running.</summary>
    public void AppendToolOutput(string delta)
    {
        if (!IsTool || string.IsNullOrEmpty(delta))
        {
            return;
        }

        Content += delta;
        if (_toolDetailFull.Length < MaxToolDetailDisplayChars * 2)
        {
            _toolDetailFull += delta;
            RefreshToolDetailDisplay();
        }
    }

    private void AssignToolDetail(string detail)
    {
        _toolDetailFull = detail ?? string.Empty;
        RefreshToolDetailDisplay();
    }

    private void RefreshToolDetailDisplay()
    {
        var limit = IsExpanded ? MaxToolDetailDisplayChars : ToolDetailPreviewChars;
        var display = TruncateToolDetailForDisplay(_toolDetailFull, limit);
        ToolDetail = display;
        ToolDetailDisplay = display;
    }

    internal static string TruncateToolDetailForDisplay(string detail, int maxChars)
    {
        if (string.IsNullOrEmpty(detail) || detail.Length <= maxChars)
        {
            return detail;
        }

        return detail[..maxChars] + "\n…";
    }

    private static string AppendCompactionDisplayNotice(string summary)
    {
        var notice = Strings.Get("Chat_CompactionNotice");
        return string.IsNullOrWhiteSpace(summary) ? notice : $"{summary}\n\n{notice}";
    }

    public static bool IsAssistantToolCallsOnly(ChatMessage message) =>
        message.Role == MessageRole.Assistant
        && !string.IsNullOrWhiteSpace(message.ToolCallsJson)
        && string.IsNullOrWhiteSpace(message.Content)
        && string.IsNullOrWhiteSpace(message.ReasoningContent);

}
