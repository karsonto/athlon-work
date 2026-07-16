using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.App.Services;

public sealed class LayoutCoordinator
{
    private readonly UiLayoutSettingsBridge _uiLayout;
    private readonly AppSettings _appSettings;

    public LayoutCoordinator(IFileStorageService storage, AppSettings appSettings)
    {
        _appSettings = appSettings;
        _uiLayout = new UiLayoutSettingsBridge(storage, appSettings);
    }

    public UiSettings Ui => _appSettings.Ui;

    public void ClampInitialLayout() => _uiLayout.ClampInitialLayout();

    public Task PersistNowAsync() => _uiLayout.PersistNowAsync();

    public void Dispose() => _uiLayout.Dispose();

    public void SetContextSidebarVisible(bool visible, Action onLayoutChanged)
    {
        var ui = _appSettings.Ui;
        if (ui.ContextSidebarVisible == visible)
        {
            return;
        }

        ui.ContextSidebarVisible = visible;
        if (visible && ui.ContextSidebarWidth < UiLayoutConstraints.ContextSidebarMinWidth)
        {
            ui.ContextSidebarWidth = UiLayoutConstraints.ContextSidebarDefaultWidth;
        }

        onLayoutChanged();
    }

    public void SetNavigationSidebarVisible(bool visible, Action onLayoutChanged)
    {
        var ui = _appSettings.Ui;
        if (ui.NavigationSidebarVisible == visible)
        {
            return;
        }

        ui.NavigationSidebarVisible = visible;
        if (visible && ui.NavigationSidebarWidth < UiLayoutConstraints.NavigationSidebarMinWidth)
        {
            ui.NavigationSidebarWidth = UiLayoutConstraints.NavigationSidebarDefaultWidth;
        }

        onLayoutChanged();
    }

    public void UpdateContextSidebarWidth(double width)
    {
        if (!_appSettings.Ui.ContextSidebarVisible)
        {
            return;
        }

        _uiLayout.TryUpdateDimension(
            _appSettings.Ui.ContextSidebarWidth,
            width,
            UiLayoutConstraints.ContextSidebarMinWidth,
            UiLayoutConstraints.ContextSidebarMaxWidth,
            value => _appSettings.Ui.ContextSidebarWidth = value);
    }

    public void UpdateNavigationSidebarWidth(double width)
    {
        if (!_appSettings.Ui.NavigationSidebarVisible)
        {
            return;
        }

        _uiLayout.TryUpdateDimension(
            _appSettings.Ui.NavigationSidebarWidth,
            width,
            UiLayoutConstraints.NavigationSidebarMinWidth,
            UiLayoutConstraints.NavigationSidebarMaxWidth,
            value => _appSettings.Ui.NavigationSidebarWidth = value);
    }

    public void UpdateEditorPaneWidth(double width) =>
        _uiLayout.TryUpdateDimension(
            _appSettings.Ui.EditorPaneWidth,
            width,
            UiLayoutConstraints.EditorPaneMinWidth,
            UiLayoutConstraints.EditorPaneMaxWidth,
            value => _appSettings.Ui.EditorPaneWidth = value);

    public void UpdateComposerHeight(double height) =>
        _uiLayout.TryUpdateDimension(
            _appSettings.Ui.ComposerHeight,
            height,
            UiLayoutConstraints.ComposerMinHeight,
            UiLayoutConstraints.ComposerMaxHeight,
            value => _appSettings.Ui.ComposerHeight = value);
}
