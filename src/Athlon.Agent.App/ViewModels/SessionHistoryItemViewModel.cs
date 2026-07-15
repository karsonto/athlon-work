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
        UpdatedAt = entry.UpdatedAt;
        UpdatedAtText = AppTimeZone.ToChina(entry.UpdatedAt).ToString("MM-dd HH:mm");
        RelativeTimeText = FormatRelativeTime(entry.UpdatedAt);
        ActiveWorkspace = entry.ActiveWorkspace;
        IsActive = isActive;
        IsRunning = isRunning;
        _stopSession = stopSession;
        _messageCount = entry.MessageCount ?? SessionMetaReader.TryReadMessageCount(entry.Path);
    }

    public string Id { get; }
    public string Title { get; }
    public DateTimeOffset UpdatedAt { get; }
    public string UpdatedAtText { get; }
    public string RelativeTimeText { get; }
    public string? ActiveWorkspace { get; }
    public bool IsActive { get; }

    [ObservableProperty]
    private bool isRunning;

    public bool CanStop => IsRunning;

    /// <summary>Sidebar secondary line: running state, otherwise relative time.</summary>
    public string MetaText => IsRunning
        ? "生成中…"
        : RelativeTimeText;

    public string StatusText => IsRunning
        ? "生成中…"
        : _messageCount > 0
            ? $"已完成 • {_messageCount} 条消息"
            : UpdatedAtText;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void StopSession() => _stopSession?.Invoke(Id);

    internal static string FormatRelativeTime(DateTimeOffset updatedAt)
    {
        var elapsed = AppTimeZone.Now - AppTimeZone.ToChina(updatedAt);
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        if (elapsed.TotalMinutes < 1)
        {
            return "now";
        }

        if (elapsed.TotalHours < 1)
        {
            return $"{Math.Max(1, (int)elapsed.TotalMinutes)}m";
        }

        if (elapsed.TotalDays < 1)
        {
            return $"{Math.Max(1, (int)elapsed.TotalHours)}h";
        }

        if (elapsed.TotalDays < 30)
        {
            return $"{Math.Max(1, (int)elapsed.TotalDays)}d";
        }

        var months = Math.Max(1, (int)(elapsed.TotalDays / 30));
        return $"{months}mo";
    }
}
