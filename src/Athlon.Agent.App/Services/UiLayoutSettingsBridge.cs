using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.App.Services;

/// <summary>Debounced persistence for splitter and composer layout dimensions.</summary>
public sealed class UiLayoutSettingsBridge : IDisposable
{
    private readonly IFileStorageService _storage;
    private readonly AppSettings _appSettings;
    private CancellationTokenSource? _saveCts;

    public UiLayoutSettingsBridge(IFileStorageService storage, AppSettings appSettings)
    {
        _storage = storage;
        _appSettings = appSettings;
    }

    public void ClampInitialLayout()
    {
        _appSettings.Ui.ContextSidebarWidth = Math.Clamp(
            _appSettings.Ui.ContextSidebarWidth,
            UiLayoutConstraints.ContextSidebarMinWidth,
            UiLayoutConstraints.ContextSidebarMaxWidth);
        if (_appSettings.Ui.ContextSidebarWidth < UiLayoutConstraints.ContextSidebarMinWidth)
        {
            _appSettings.Ui.ContextSidebarWidth = UiLayoutConstraints.ContextSidebarDefaultWidth;
        }

        _appSettings.Ui.NavigationSidebarWidth = Math.Clamp(
            _appSettings.Ui.NavigationSidebarWidth,
            UiLayoutConstraints.NavigationSidebarMinWidth,
            UiLayoutConstraints.NavigationSidebarMaxWidth);
        if (_appSettings.Ui.NavigationSidebarWidth < UiLayoutConstraints.NavigationSidebarMinWidth)
        {
            _appSettings.Ui.NavigationSidebarWidth = UiLayoutConstraints.NavigationSidebarDefaultWidth;
        }

        _appSettings.Ui.EditorPaneWidth = Math.Clamp(
            _appSettings.Ui.EditorPaneWidth,
            UiLayoutConstraints.EditorPaneMinWidth,
            UiLayoutConstraints.EditorPaneMaxWidth);
        if (_appSettings.Ui.EditorPaneWidth < UiLayoutConstraints.EditorPaneMinWidth)
        {
            _appSettings.Ui.EditorPaneWidth = UiLayoutConstraints.EditorPaneDefaultWidth;
        }

        _appSettings.Ui.ComposerHeight = Math.Clamp(
            _appSettings.Ui.ComposerHeight,
            UiLayoutConstraints.ComposerMinHeight,
            UiLayoutConstraints.ComposerMaxHeight);
        if (_appSettings.Ui.ComposerHeight < UiLayoutConstraints.ComposerMinHeight)
        {
            _appSettings.Ui.ComposerHeight = UiLayoutConstraints.ComposerDefaultHeight;
        }
    }

    public bool TryUpdateDimension(
        double currentValue,
        double requestedValue,
        double minValue,
        double maxValue,
        Action<double> apply)
    {
        var clamped = Math.Clamp(requestedValue, minValue, maxValue);
        if (Math.Abs(currentValue - clamped) < 0.5)
        {
            return false;
        }

        apply(clamped);
        SchedulePersist();
        return true;
    }

    public Task PersistNowAsync() => _storage.SaveSettingsAsync(_appSettings);

    public void SchedulePersist()
    {
        _saveCts?.Cancel();
        _saveCts?.Dispose();
        _saveCts = new CancellationTokenSource();
        var token = _saveCts.Token;
        _ = PersistDebouncedAsync(token);
    }

    private async Task PersistDebouncedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(400, cancellationToken);
            await _storage.SaveSettingsAsync(_appSettings);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer layout change.
        }
    }

    public void Dispose()
    {
        _saveCts?.Cancel();
        _saveCts?.Dispose();
        _saveCts = null;
    }
}
