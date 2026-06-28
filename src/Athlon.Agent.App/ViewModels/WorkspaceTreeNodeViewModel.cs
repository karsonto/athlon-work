using System.Collections.ObjectModel;
using System.IO;
using Athlon.Agent.App.Services;

namespace Athlon.Agent.App.ViewModels;

public sealed class WorkspaceTreeNodeViewModel
{
    private bool _childrenLoaded;

    private WorkspaceTreeNodeViewModel(string name, string? fullPath, bool isDirectory, bool isPlaceholder, bool isExpanderPlaceholder = false)
    {
        Name = name;
        FullPath = fullPath;
        IsDirectory = isDirectory;
        IsPlaceholder = isPlaceholder;
        IsExpanderPlaceholder = isExpanderPlaceholder;
        IconKind = WorkspaceFileIconResolver.Resolve(name, fullPath, isDirectory, isPlaceholder && !isExpanderPlaceholder);
        Children = new ObservableCollection<WorkspaceTreeNodeViewModel>();
    }

    public string Name { get; }
    public string? FullPath { get; }
    public bool IsDirectory { get; }
    public bool IsPlaceholder { get; }
    public bool IsExpanderPlaceholder { get; }
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
        if (_childrenLoaded || IsPlaceholder || !IsDirectory || string.IsNullOrWhiteSpace(FullPath))
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
        return ignorePatterns.Any(pattern => string.Equals(name, pattern, StringComparison.OrdinalIgnoreCase));
    }
}
