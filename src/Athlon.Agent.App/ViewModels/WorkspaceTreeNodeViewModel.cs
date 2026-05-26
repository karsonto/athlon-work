using System.Collections.ObjectModel;
using System.IO;

namespace Athlon.Agent.App.ViewModels;

public sealed class WorkspaceTreeNodeViewModel
{
    private WorkspaceTreeNodeViewModel(string name, string? fullPath, bool isDirectory, bool isPlaceholder)
    {
        Name = name;
        FullPath = fullPath;
        IsDirectory = isDirectory;
        IsPlaceholder = isPlaceholder;
        Icon = isPlaceholder ? "○" : isDirectory ? "📁" : "📄";
        Children = new ObservableCollection<WorkspaceTreeNodeViewModel>();
    }

    public string Name { get; }
    public string? FullPath { get; }
    public bool IsDirectory { get; }
    public bool IsPlaceholder { get; }
    public string Icon { get; }
    public ObservableCollection<WorkspaceTreeNodeViewModel> Children { get; }

    public static WorkspaceTreeNodeViewModel Placeholder(string message) =>
        new(message, null, false, true);

    public static WorkspaceTreeNodeViewModel? FromDirectory(string directoryPath, IReadOnlyCollection<string> ignorePatterns, int depth, int maxDepth, ref int nodeCount, int maxNodes)
    {
        if (nodeCount >= maxNodes || depth > maxDepth)
        {
            return null;
        }

        var name = Path.GetFileName(directoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(name))
        {
            name = directoryPath;
        }

        var node = new WorkspaceTreeNodeViewModel(name, directoryPath, true, false);
        nodeCount++;

        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(directoryPath)
                .Where(path => !ShouldIgnore(path, ignorePatterns))
                .OrderBy(path => Directory.Exists(path) ? 0 : 1)
                .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return node;
        }

        foreach (var entry in entries)
        {
            if (nodeCount >= maxNodes)
            {
                node.Children.Add(Placeholder("…"));
                break;
            }

            if (Directory.Exists(entry))
            {
                var childDirectory = FromDirectory(entry, ignorePatterns, depth + 1, maxDepth, ref nodeCount, maxNodes);
                if (childDirectory is not null)
                {
                    node.Children.Add(childDirectory);
                }
            }
            else
            {
                node.Children.Add(new WorkspaceTreeNodeViewModel(Path.GetFileName(entry), entry, false, false));
                nodeCount++;
            }
        }

        return node;
    }

    public static ObservableCollection<WorkspaceTreeNodeViewModel> BuildTree(string? rootPath, IReadOnlyCollection<string> ignorePatterns, int maxDepth = 5, int maxNodes = 500)
    {
        var tree = new ObservableCollection<WorkspaceTreeNodeViewModel>();
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
        {
            tree.Add(Placeholder("未配置工作区"));
            return tree;
        }

        var nodeCount = 0;
        var root = FromDirectory(Path.GetFullPath(rootPath), ignorePatterns, 0, maxDepth, ref nodeCount, maxNodes);
        if (root is not null)
        {
            tree.Add(root);
        }

        return tree;
    }

    private static bool ShouldIgnore(string path, IReadOnlyCollection<string> ignorePatterns)
    {
        var name = Path.GetFileName(path);
        return ignorePatterns.Any(pattern => string.Equals(name, pattern, StringComparison.OrdinalIgnoreCase));
    }
}
