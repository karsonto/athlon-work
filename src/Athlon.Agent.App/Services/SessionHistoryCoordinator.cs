using System.Collections.ObjectModel;
using Athlon.Agent.App.Localization;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.App.Services;

/// <summary>Session list refresh, debouncing, and title derivation.</summary>
public sealed class SessionHistoryCoordinator : IDisposable
{
    private static readonly TimeSpan RefreshDebounce = TimeSpan.FromMilliseconds(300);

    private readonly IFileStorageService _storage;
    private CancellationTokenSource? _refreshCts;
    private string? _currentSessionId;
    private Func<string, bool>? _isSessionRunning;
    private Action<string>? _stopSession;

    public SessionHistoryCoordinator(IFileStorageService storage)
    {
        _storage = storage;
        AgentRecordGroups = new ObservableCollection<AgentRecordGroupViewModel>();
        AppCultureManager.CultureChanged += OnCultureChanged;
    }

    public ObservableCollection<AgentRecordGroupViewModel> AgentRecordGroups { get; }

    public bool HasAgentRecords => AgentRecordGroups.Count > 0;

    public void RequestRefresh(Func<Task> refreshAction)
    {
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = new CancellationTokenSource();
        var token = _refreshCts.Token;
        _ = DebouncedRefreshAsync(refreshAction, token);
    }

    public async Task RefreshAsync(
        string currentSessionId,
        Func<string, bool> isSessionRunning,
        Action<string>? stopSession)
    {
        _currentSessionId = currentSessionId;
        _isSessionRunning = isSessionRunning;
        _stopSession = stopSession;

        var entries = await _storage.ListSessionsAsync();
        var previouslyExpanded = AgentRecordGroups
            .Where(group => group.IsExpanded)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        AgentRecordGroups.Clear();
        foreach (var group in AgentRecordGrouping.Build(
                     entries,
                     currentSessionId,
                     isSessionRunning,
                     stopSession,
                     previouslyExpanded.Count > 0 ? previouslyExpanded : null))
        {
            if (group.Items.Count > 0)
            {
                AgentRecordGroups.Add(group);
            }
        }
    }

    public static AgentSession DeriveSessionTitle(AgentSession session)
    {
        if (!string.Equals(session.Title, "New Chat", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(session.Title, "New chat", StringComparison.OrdinalIgnoreCase))
        {
            return session;
        }

        var firstUser = session.Messages.FirstOrDefault(message => message.Role == MessageRole.User);
        if (firstUser is null || string.IsNullOrWhiteSpace(firstUser.Content))
        {
            return session;
        }

        var normalized = firstUser.Content.Replace("\r\n", " ").Replace('\n', ' ').Trim();
        var title = normalized.Length <= 30 ? normalized : $"{normalized[..30]}...";
        return session.WithTitle(title);
    }

    public SessionHistoryItemViewModel? GetFirstAgentRecord()
    {
        foreach (var group in AgentRecordGroups)
        {
            if (group.Items.Count > 0)
            {
                return group.Items[0];
            }
        }

        return null;
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        if (_currentSessionId is null || _isSessionRunning is null)
        {
            return;
        }

        _ = RefreshAsync(_currentSessionId, _isSessionRunning, _stopSession);
    }

    private static async Task DebouncedRefreshAsync(Func<Task> refreshAction, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(RefreshDebounce, cancellationToken);
            await refreshAction();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer refresh request.
        }
    }

    public void Dispose()
    {
        AppCultureManager.CultureChanged -= OnCultureChanged;
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = null;
    }
}
