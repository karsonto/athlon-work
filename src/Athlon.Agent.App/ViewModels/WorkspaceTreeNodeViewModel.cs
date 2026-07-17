using System.Collections.ObjectModel;
using System.IO;
using Athlon.Agent.App.Services;
using Athlon.Agent.Core;

namespace Athlon.Agent.App.ViewModels;

public sealed class WorkspaceTreeNodeViewModel
{
    private bool _childrenLoaded;

    private WorkspaceTreeNodeViewModel(
        string name,
        string? fullPath,
        bool isDirectory,
        bool isPlaceholder,
        bool isExpanderPlaceholder = false,
        bool isRemote = false)
    {
        Name = name;
        FullPath = fullPath;
        IsDirectory = isDirectory;
        IsPlaceholder = isPlaceholder;
        IsExpanderPlaceholder = isExpanderPlaceholder;
        IsRemote = isRemote;
        IconKind = WorkspaceFileIconResolver.Resolve(name, fullPath, isDirectory, isPlaceholder && !isExpanderPlaceholder);
        Children = new ObservableCollection<WorkspaceTreeNodeViewModel>();
    }

    public string Name { get; }
    public string? FullPath { get; }
    public bool IsDirectory { get; }
    public bool IsPlaceholder { get; }
    public bool IsExpanderPlaceholder { get; }
    public bool IsRemote { get; }
    public WorkspaceFileIconKind IconKind { get; }
    public string OpenInExplorerMenuHeader => IsDirectory ? "打开该目录" : "打开文件所在文件夹";
    public bool IsExpanded { get; set; }
    public ObservableCollection<WorkspaceTreeNodeViewModel> Children { get; }

    public static WorkspaceTreeNodeViewModel Placeholder(string message) =>
        new(message, null, false, true);

    private static WorkspaceTreeNodeViewModel ExpanderPlaceholder() =>
        new(string.Empty, null, false, true, isExpanderPlaceholder: true);

    public void EnsureChildrenLoaded(IReadOnlyCollection<string> ignorePatterns, int maxEntries = 2000)
    {
        if (_childrenLoaded || IsRemote || IsPlaceholder || !IsDirectory || string.IsNullOrWhiteSpace(FullPath))
        {
            return;
        }

        _childrenLoaded = true;
        Children.Clear();

        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(FullPath)
                .Where(path => !ShouldIgnore(path, ignorePatterns))
                .OrderBy(path => Directory.Exists(path) ? 0 : 1)
                .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return;
        }

        var count = 0;
        foreach (var entry in entries)
        {
            if (count >= maxEntries)
            {
                Children.Add(Placeholder("…"));
                break;
            }

            count++;
            if (Directory.Exists(entry))
            {
                var child = CreateDirectoryNode(entry);
                if (DirectoryMayHaveChildren(entry, ignorePatterns))
                {
                    child.Children.Add(ExpanderPlaceholder());
                }

                Children.Add(child);
            }
            else
            {
                Children.Add(new WorkspaceTreeNodeViewModel(Path.GetFileName(entry), entry, false, false));
            }
        }
    }

    public async Task EnsureRemoteChildrenLoadedAsync(
        Func<string, CancellationToken, Task<IReadOnlyList<SshEntry>>> listAsync,
        IReadOnlyCollection<string> ignorePatterns,
        int maxEntries = 2000,
        CancellationToken cancellationToken = default)
    {
        if (_childrenLoaded || !IsRemote || IsPlaceholder || !IsDirectory || string.IsNullOrWhiteSpace(FullPath))
        {
            return;
        }

        _childrenLoaded = true;
        Children.Clear();

        IReadOnlyList<SshEntry> entries;
        try
        {
            entries = await listAsync(FullPath, cancellationToken).ConfigureAwait(true);
        }
        catch
        {
            Children.Add(Placeholder("…"));
            return;
        }

        var count = 0;
        foreach (var entry in entries
                     .OrderBy(item => item.IsDirectory ? 0 : 1)
                     .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (ShouldIgnoreName(entry.Name, ignorePatterns))
            {
                continue;
            }

            if (count >= maxEntries)
            {
                Children.Add(Placeholder("…"));
                break;
            }

            count++;
            if (entry.IsDirectory)
            {
                var child = new WorkspaceTreeNodeViewModel(
                    entry.Name,
                    entry.FullPath,
                    isDirectory: true,
                    isPlaceholder: false,
                    isRemote: true);
                child.Children.Add(ExpanderPlaceholder());
                Children.Add(child);
            }
            else
            {
                Children.Add(new WorkspaceTreeNodeViewModel(
                    entry.Name,
                    entry.FullPath,
                    isDirectory: false,
                    isPlaceholder: false,
                    isRemote: true));
            }
        }
    }

    public static ObservableCollection<WorkspaceTreeNodeViewModel> BuildTree(string? rootPath, IReadOnlyCollection<string> ignorePatterns)
    {
        var tree = new ObservableCollection<WorkspaceTreeNodeViewModel>();
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
        {
            return tree;
        }

        var root = CreateDirectoryNode(Path.GetFullPath(rootPath));
        root.IsExpanded = true;
        root.EnsureChildrenLoaded(ignorePatterns);
        tree.Add(root);
        return tree;
    }

    public static ObservableCollection<WorkspaceTreeNodeViewModel> BuildRemoteTree(
        string rootPath,
        string displayName,
        IReadOnlyList<SshEntry> entries,
        IReadOnlyCollection<string> ignorePatterns)
    {
        var tree = new ObservableCollection<WorkspaceTreeNodeViewModel>();
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return tree;
        }

        var root = new WorkspaceTreeNodeViewModel(
            string.IsNullOrWhiteSpace(displayName) ? rootPath : displayName,
            rootPath,
            isDirectory: true,
            isPlaceholder: false,
            isRemote: true)
        {
            IsExpanded = true
        };
        root._childrenLoaded = true;
        foreach (var entry in entries
                     .OrderBy(item => item.IsDirectory ? 0 : 1)
                     .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (ShouldIgnoreName(entry.Name, ignorePatterns))
            {
                continue;
            }

            if (entry.IsDirectory)
            {
                var child = new WorkspaceTreeNodeViewModel(
                    entry.Name,
                    entry.FullPath,
                    isDirectory: true,
                    isPlaceholder: false,
                    isRemote: true);
                // Placeholder keeps the expand arrow visible until SSH children are loaded.
                child.Children.Add(ExpanderPlaceholder());
                root.Children.Add(child);
            }
            else
            {
                root.Children.Add(new WorkspaceTreeNodeViewModel(
                    entry.Name,
                    entry.FullPath,
                    isDirectory: false,
                    isPlaceholder: false,
                    isRemote: true));
            }
        }

        tree.Add(root);
        return tree;
    }

    private static WorkspaceTreeNodeViewModel CreateDirectoryNode(string directoryPath)
    {
        var name = Path.GetFileName(directoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(name))
        {
            name = directoryPath;
        }

        return new WorkspaceTreeNodeViewModel(name, directoryPath, true, false);
    }

    private static bool DirectoryMayHaveChildren(string directoryPath, IReadOnlyCollection<string> ignorePatterns)
    {
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directoryPath))
            {
                if (!ShouldIgnore(entry, ignorePatterns))
                {
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool ShouldIgnore(string path, IReadOnlyCollection<string> ignorePatterns)
    {
        var name = Path.GetFileName(path);
        return ShouldIgnoreName(name, ignorePatterns);
    }

    private static bool ShouldIgnoreName(string name, IReadOnlyCollection<string> ignorePatterns) =>
        ignorePatterns.Any(pattern => string.Equals(name, pattern, StringComparison.OrdinalIgnoreCase));
}
