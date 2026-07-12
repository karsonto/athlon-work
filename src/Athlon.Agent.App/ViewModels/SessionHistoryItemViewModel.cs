using Athlon.Agent.App.Services;
using Athlon.Agent.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Athlon.Agent.App.ViewModels;

public sealed partial class SessionHistoryItemViewModel : ObservableObject
{
    private readonly Action<string>? _stopSession;
    private readonly int _messageCount;

    public SessionHistoryItemViewModel(
        SessionIndexEntry entry,
        bool isActive,
        bool isRunning,
        Action<string>? stopSession)
    {
        Id = entry.Id;
        Title = string.IsNullOrWhiteSpace(entry.Title) ? "未命名 Agent" : entry.Title;
        UpdatedAtText = AppTimeZone.ToChina(entry.UpdatedAt).ToString("MM-dd HH:mm");
        IsActive = isActive;
        IsRunning = isRunning;
        _stopSession = stopSession;
        _messageCount = entry.MessageCount ?? SessionMetaReader.TryReadMessageCount(entry.Path);
    }

    public string Id { get; }
    public string Title { get; }
    public string UpdatedAtText { get; }
    public bool IsActive { get; }

    [ObservableProperty]
    private bool isRunning;

    public bool CanStop => IsRunning;

    public string MetaText => IsRunning
        ? "生成中…"
        : _messageCount > 0
            ? $"已完成 • {_messageCount} 条消息"
            : UpdatedAtText;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void StopSession() => _stopSession?.Invoke(Id);
}
