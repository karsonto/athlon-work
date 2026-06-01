using Athlon.Agent.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Athlon.Agent.App.ViewModels;

public sealed partial class SessionHistoryItemViewModel : ObservableObject
{
    private readonly Action<string>? _stopSession;

    public SessionHistoryItemViewModel(
        SessionIndexEntry entry,
        bool isActive,
        bool isRunning,
        Action<string>? stopSession)
    {
        Id = entry.Id;
        Title = string.IsNullOrWhiteSpace(entry.Title) ? "未命名 Agent" : entry.Title;
        UpdatedAtText = entry.UpdatedAt.ToLocalTime().ToString("MM-dd HH:mm");
        IsActive = isActive;
        IsRunning = isRunning;
        _stopSession = stopSession;
    }

    public string Id { get; }
    public string Title { get; }
    public string UpdatedAtText { get; }
    public bool IsActive { get; }

    [ObservableProperty]
    private bool isRunning;

    public bool CanStop => IsRunning;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void StopSession() => _stopSession?.Invoke(Id);
}
