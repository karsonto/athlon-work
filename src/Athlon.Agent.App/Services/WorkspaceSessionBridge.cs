using System.IO;
using System.Windows.Threading;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.App.Services;

/// <summary>Workspace context sync and filesystem watching for the active session.</summary>
public sealed class WorkspaceSessionBridge : IDisposable
{
    private static readonly TimeSpan WatcherDebounceInterval = TimeSpan.FromMilliseconds(400);

    private FileSystemWatcher? _workspaceWatcher;
    private DispatcherTimer? _watcherDebounceTimer;
    private Action? _pendingTreeRefresh;
    private string? _pendingExternalFileChange;

    public void SyncWorkspaceContext(
        AgentSession session,
        AppSettings appSettings,
        IActiveWorkspaceContext workspaceContext)
    {
        if (!string.IsNullOrWhiteSpace(session.ActiveWorkspace))
        {
            var activeRoot = Path.GetFullPath(session.ActiveWorkspace);
            var match = appSettings.Workspaces.FirstOrDefault(workspace =>
                !string.IsNullOrWhiteSpace(workspace.RootPath)
                && string.Equals(Path.GetFullPath(workspace.RootPath), activeRoot, StringComparison.OrdinalIgnoreCase));
            workspaceContext.SetWorkspace(activeRoot, match?.Name, ResolveIgnorePatterns(match, appSettings));
            return;
        }

        var configured = appSettings.Workspaces.FirstOrDefault(workspace => !string.IsNullOrWhiteSpace(workspace.RootPath));
        if (configured is null)
        {
            workspaceContext.SetWorkspace(null);
            return;
        }

        workspaceContext.SetWorkspace(configured.RootPath, configured.Name, ResolveIgnorePatterns(configured, appSettings));
    }

    public void ConfigureWatcher(
        AgentSession session,
        Action<string> onExternalFileChange,
        Action onWorkspaceTreeRefresh)
    {
        _workspaceWatcher?.Dispose();
        _workspaceWatcher = null;
        StopWatcherDebounceTimer();

        if (string.IsNullOrWhiteSpace(session.ActiveWorkspace) || !Directory.Exists(session.ActiveWorkspace))
        {
            return;
        }

        _workspaceWatcher = new FileSystemWatcher(session.ActiveWorkspace)
        {
            IncludeSubdirectories = true,
            EnableRaisingEvents = true
        };

        void Handler(object sender, FileSystemEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(e.FullPath))
            {
                _pendingExternalFileChange = e.FullPath;
            }

            _pendingTreeRefresh = onWorkspaceTreeRefresh;
            ScheduleWatcherDebounce(onExternalFileChange);
        }

        _workspaceWatcher.Created += Handler;
        _workspaceWatcher.Deleted += Handler;
        _workspaceWatcher.Changed += Handler;
        _workspaceWatcher.Renamed += Handler;
    }

    public static IReadOnlyList<string> ResolveIgnorePatterns(WorkspaceSettings? workspace, AppSettings appSettings) =>
        WorkspaceIgnoreResolver.Resolve(
            workspacePatterns: workspace?.IgnorePatterns,
            globalPatterns: appSettings.WorkspaceIgnore.DirectoryNames);

    public static bool TryGetActiveWorkspaceRoot(AgentSession session, out string root)
    {
        root = string.Empty;
        if (string.IsNullOrWhiteSpace(session.ActiveWorkspace) || !Directory.Exists(session.ActiveWorkspace))
        {
            return false;
        }

        root = Path.GetFullPath(session.ActiveWorkspace);
        return true;
    }

    public static bool IsWorkspaceRootPath(string workspaceRoot, string targetPath)
    {
        var root = NormalizeDirectoryPath(workspaceRoot);
        var target = NormalizeDirectoryPath(targetPath);
        return string.Equals(root, target, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsPathUnderWorkspace(string workspaceRoot, string targetPath)
    {
        var root = NormalizeDirectoryPath(workspaceRoot);
        var target = Path.GetFullPath(targetPath);
        if (string.Equals(root, target, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var rootPrefix = root + Path.DirectorySeparatorChar;
        return target.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDirectoryPath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private void ScheduleWatcherDebounce(Action<string> onExternalFileChange)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            FlushWatcherDebounce(onExternalFileChange);
            return;
        }

        if (_watcherDebounceTimer is null)
        {
            _watcherDebounceTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
            {
                Interval = WatcherDebounceInterval
            };
            _watcherDebounceTimer.Tick += (_, _) =>
            {
                StopWatcherDebounceTimer();
                FlushWatcherDebounce(onExternalFileChange);
            };
        }

        _watcherDebounceTimer.Stop();
        _watcherDebounceTimer.Start();
    }

    private void FlushWatcherDebounce(Action<string> onExternalFileChange)
    {
        var externalPath = _pendingExternalFileChange;
        var treeRefresh = _pendingTreeRefresh;
        _pendingExternalFileChange = null;
        _pendingTreeRefresh = null;

        if (!string.IsNullOrWhiteSpace(externalPath))
        {
            onExternalFileChange(externalPath);
        }

        treeRefresh?.Invoke();
    }

    private void StopWatcherDebounceTimer()
    {
        if (_watcherDebounceTimer is null)
        {
            return;
        }

        _watcherDebounceTimer.Stop();
        _watcherDebounceTimer = null;
    }

    public void Dispose()
    {
        StopWatcherDebounceTimer();
        _workspaceWatcher?.Dispose();
        _workspaceWatcher = null;
    }
}
