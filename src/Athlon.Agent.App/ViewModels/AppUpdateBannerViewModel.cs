using System.Windows.Threading;
using Athlon.Agent.App.Localization;
using Athlon.Agent.App.Resources;
using Athlon.Agent.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Athlon.Agent.App.ViewModels;

/// <summary>Cursor-style bottom-left update banner with quiet periodic checks.</summary>
public sealed partial class AppUpdateBannerViewModel : ObservableObject, IDisposable
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(3);
    public static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(4);

    private readonly AppUpdateService _updateService;
    private readonly ILocalizationService _loc;
    private readonly Action<string, ShellToastKind> _showToast;
    private readonly DispatcherTimer _timer;
    private CancellationTokenSource? _checkCts;
    private AppUpdateQuietResult? _pending;
    private bool _dismissedThisSession;
    private bool _started;
    private bool _disposed;

    public AppUpdateBannerViewModel(
        AppUpdateService updateService,
        ILocalizationService localization,
        Action<string, ShellToastKind> showToast)
    {
        _updateService = updateService;
        _loc = localization;
        _showToast = showToast;
        _timer = new DispatcherTimer { Interval = CheckInterval };
        _timer.Tick += OnTimerTick;
    }

    [ObservableProperty]
    private bool isUpdateBannerVisible;

    [ObservableProperty]
    private string updateBannerText = string.Empty;

    [ObservableProperty]
    private bool isInstallingUpdate;

    public bool CanInstallUpdate => !IsInstallingUpdate && _pending is not null;

    public bool HasPendingUpdate => _pending is not null;

    partial void OnIsUpdateBannerVisibleChanged(bool value) =>
        InstallUpdateCommand.NotifyCanExecuteChanged();

    partial void OnIsInstallingUpdateChanged(bool value)
    {
        InstallUpdateCommand.NotifyCanExecuteChanged();
        InstallUpdateFromMenuCommand.NotifyCanExecuteChanged();
    }

    public void Start()
    {
        if (_started || _disposed)
        {
            return;
        }

#if DEBUG
        if (string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable(AppUpdateCoordinator.UpdateUrlEnvironmentVariable)))
        {
            App.StartupTrace("Update banner polling skipped in DEBUG without ATHLON_UPDATE_URL.");
            return;
        }
#endif

        _started = true;
        _ = RunQuietCheckAsync(InitialDelay);
        _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
        _checkCts?.Cancel();
    }

    [RelayCommand]
    private void DismissUpdateBanner()
    {
        _dismissedThisSession = true;
        IsUpdateBannerVisible = false;
    }

    [RelayCommand(CanExecute = nameof(CanInstallUpdate))]
    private async Task InstallUpdateAsync()
    {
        if (_pending is null || IsInstallingUpdate)
        {
            return;
        }

        IsInstallingUpdate = true;
        _showToast(_loc["Update_Applying"], ShellToastKind.Info);
        try
        {
            await _updateService.ApplyAsync(_pending).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            IsInstallingUpdate = false;
            App.StartupTrace($"Update apply failed: {ex.Message}");
            _showToast(_loc.Format("Update_InstallFailed", ex.Message), ShellToastKind.Error);
        }
    }

    /// <summary>Help menu: check for updates and install when available (Cursor-style manual entry).</summary>
    [RelayCommand(CanExecute = nameof(CanInstallUpdateFromMenu))]
    private async Task InstallUpdateFromMenuAsync()
    {
        if (IsInstallingUpdate)
        {
            return;
        }

        if (_pending is null)
        {
            try
            {
                var result = await _updateService.CheckQuietAsync().ConfigureAwait(true);
                if (result is null)
                {
                    _showToast(_loc["Update_UpToDate"], ShellToastKind.Info);
                    return;
                }

                SetPendingUpdate(result, showBanner: !_dismissedThisSession);
            }
            catch (Exception ex)
            {
                App.StartupTrace($"Update menu check failed: {ex.Message}");
                _showToast(_loc["Update_UpToDate"], ShellToastKind.Info);
                return;
            }
        }

        await InstallUpdateAsync().ConfigureAwait(true);
    }

    private bool CanInstallUpdateFromMenu => !IsInstallingUpdate;

    private void SetPendingUpdate(AppUpdateQuietResult result, bool showBanner)
    {
        _pending = result;
        UpdateBannerText = string.IsNullOrWhiteSpace(result.Version)
            ? _loc["Update_BannerTitle"]
            : _loc.Format("Update_BannerTitleWithVersion", result.Version);
        if (showBanner)
        {
            IsUpdateBannerVisible = true;
        }

        OnPropertyChanged(nameof(HasPendingUpdate));
        InstallUpdateCommand.NotifyCanExecuteChanged();
    }

    private void OnTimerTick(object? sender, EventArgs e) =>
        _ = RunQuietCheckAsync(TimeSpan.Zero);

    private async Task RunQuietCheckAsync(TimeSpan delay)
    {
        if (_disposed || _dismissedThisSession)
        {
            return;
        }

        _checkCts?.Cancel();
        _checkCts?.Dispose();
        _checkCts = new CancellationTokenSource();
        var token = _checkCts.Token;

        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, token).ConfigureAwait(true);
            }

            if (_dismissedThisSession || token.IsCancellationRequested)
            {
                return;
            }

            var result = await _updateService.CheckQuietAsync(token).ConfigureAwait(true);
            if (_dismissedThisSession || token.IsCancellationRequested)
            {
                return;
            }

            if (result is null)
            {
                // No update / error / skipped — do not show banner; keep an already-visible banner.
                return;
            }

            SetPendingUpdate(result, showBanner: true);
        }
        catch (OperationCanceledException)
        {
            // superseded or stopped
        }
        catch (Exception ex)
        {
            App.StartupTrace($"Update banner check failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _timer.Tick -= OnTimerTick;
        _checkCts?.Dispose();
        _checkCts = null;
    }
}
