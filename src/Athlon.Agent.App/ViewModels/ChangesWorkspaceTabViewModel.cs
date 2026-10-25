using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using Athlon.Agent.App.Resources;
using Athlon.Agent.App.Services;
using Athlon.Agent.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Athlon.Agent.App.ViewModels;

public sealed partial class ChangesWorkspaceTabViewModel : WorkspaceTabViewModel
{
    private readonly HashSet<string> _collapsedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _expandedFolds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int?> _lineCounts = new(StringComparer.OrdinalIgnoreCase);

    public ChangesWorkspaceTabViewModel(string title)
        : base(Guid.NewGuid(), WorkspaceTabKind.Changes, title)
    {
        Files = new ObservableCollection<ModifiedFileViewModel>();
        Rows = new ObservableCollection<ChangesReviewRowViewModel>();
    }

    public ObservableCollection<ModifiedFileViewModel> Files { get; }

    public ObservableCollection<ChangesReviewRowViewModel> Rows { get; }

    public event EventHandler? RowsChanging;

    public event EventHandler? RowsChanged;

    /// <summary>Set while a rebuild should scroll this file's header into view.</summary>
    public string? PendingScrollPath { get; private set; }

    [ObservableProperty]
    private ModifiedFileViewModel? _selectedFile;

    [ObservableProperty]
    private string _headline = string.Empty;

    [ObservableProperty]
    private string _addedText = string.Empty;

    [ObservableProperty]
    private string _removedText = string.Empty;

    public string NoDiffText => Strings.Get("Chat_NoDiffAvailable");

    /// <summary>Card id (<c>files:{anchor}</c>) this pane is showing, so later edits in that turn refresh it.</summary>
    public string? SourceEntryId { get; private set; }

    public void ClearPendingScroll() => PendingScrollPath = null;

    public void Show(
        IReadOnlyList<ModifiedFileViewModel> files,
        string? selectedPath,
        string? sourceEntryId = null,
        string? workspaceRoot = null,
        WorkspaceKind workspaceKind = WorkspaceKind.Local,
        bool focusPath = false)
    {
        if (!string.IsNullOrWhiteSpace(sourceEntryId))
        {
            SourceEntryId = sourceEntryId;
        }

        var keep = string.IsNullOrWhiteSpace(selectedPath) ? SelectedFile?.RelativePath : selectedPath;
        PruneState(files);
        Files.Clear();
        _lineCounts.Clear();
        foreach (var file in files)
        {
            Files.Add(file);
            _lineCounts[file.RelativePath] = file.HasDiff
                ? TryCountWorkspaceLines(workspaceRoot, workspaceKind, file.RelativePath)
                : null;
        }

        var match = string.IsNullOrWhiteSpace(keep)
            ? null
            : Files.FirstOrDefault(file =>
                string.Equals(file.RelativePath, keep, StringComparison.OrdinalIgnoreCase));
        SelectedFile = match ?? Files.FirstOrDefault();
        if (focusPath && match is not null)
        {
            _collapsedFiles.Remove(match.RelativePath);
        }

        UpdateSummary();
        RebuildRows(focusPath ? match?.RelativePath : null);
    }

    private void UpdateSummary()
    {
        Headline = Files.Count == 1
            ? Strings.Get("Chat_FilesChangedOne")
            : string.Format(Strings.Get("Chat_FilesChangedMany"), Files.Count);
        var added = Files.Sum(file => file.AddedCount);
        var removed = Files.Sum(file => file.RemovedCount);
        AddedText = added > 0 ? "+" + added : string.Empty;
        RemovedText = removed > 0 ? "-" + removed : string.Empty;
    }

    private void ToggleFile(string path)
    {
        if (!_collapsedFiles.Add(path))
        {
            _collapsedFiles.Remove(path);
        }

        RebuildRows(scrollPath: null);
    }

    private void ToggleFold(string key)
    {
        if (!_expandedFolds.Add(key))
        {
            _expandedFolds.Remove(key);
        }

        RebuildRows(scrollPath: null);
    }

    private void RebuildRows(string? scrollPath)
    {
        PendingScrollPath = scrollPath;
        RowsChanging?.Invoke(this, EventArgs.Empty);
        var built = BuildRows();
        Rows.Clear();
        foreach (var row in built)
        {
            Rows.Add(row);
        }

        RowsChanged?.Invoke(this, EventArgs.Empty);
    }

    private List<ChangesReviewRowViewModel> BuildRows()
    {
        var rows = new List<ChangesReviewRowViewModel>();
        foreach (var file in Files)
        {
            var expanded = !_collapsedFiles.Contains(file.RelativePath);
            rows.Add(new ChangesFileHeaderRowViewModel
            {
                RelativePath = file.RelativePath,
                IconKind = WorkspaceFileIconResolver.Resolve(file.DisplayName, file.RelativePath, false, false),
                AddedText = file.AddedCount > 0 ? "+" + file.AddedCount : string.Empty,
                RemovedText = file.RemovedCount > 0 ? "-" + file.RemovedCount : string.Empty,
                IsExpanded = expanded,
                ToggleCommand = new RelayCommand(() => ToggleFile(file.RelativePath))
            });
            if (!expanded)
            {
                continue;
            }

            if (!file.HasDiff)
            {
                rows.Add(new ChangesNoteRowViewModel { Text = NoDiffText });
                continue;
            }

            var review = UnifiedDiffDisplayParser.BuildReview(file.UnifiedDiffText);
            if (review.Lines.Count == 0)
            {
                rows.Add(new ChangesNoteRowViewModel { Text = NoDiffText });
                continue;
            }

            foreach (var line in review.Lines)
            {
                AppendReviewLine(rows, file.RelativePath, line);
            }

            AppendTrailingGap(rows, file.RelativePath, review.LastNewLine);
        }

        return rows;
    }

    private void AppendReviewLine(List<ChangesReviewRowViewModel> rows, string path, DiffReviewLine line)
    {
        if (line.Kind == DiffLineKind.Collapsed)
        {
            var key = FoldKey(path, line);
            var expanded = line.CanExpand && _expandedFolds.Contains(key);
            rows.Add(new ChangesFoldRowViewModel
            {
                Text = string.Format(Strings.Get("Chat_UnmodifiedLines"), line.CollapsedCount ?? 0),
                CanExpand = line.CanExpand,
                IsExpanded = expanded,
                ToggleCommand = line.CanExpand ? new RelayCommand(() => ToggleFold(key)) : null
            });
            if (expanded && line.HiddenLines is not null)
            {
                foreach (var hidden in line.HiddenLines)
                {
                    rows.Add(CreateCodeRow(path, hidden));
                }
            }

            return;
        }

        rows.Add(CreateCodeRow(path, line));
    }

    private void AppendTrailingGap(List<ChangesReviewRowViewModel> rows, string path, int? lastNewLine)
    {
        if (lastNewLine is not int last)
        {
            return;
        }

        if (!_lineCounts.TryGetValue(path, out var counted) || counted is not int total)
        {
            return;
        }

        var gap = total - last;
        if (gap <= 0)
        {
            return;
        }

        rows.Add(new ChangesFoldRowViewModel
        {
            Text = string.Format(Strings.Get("Chat_UnmodifiedLines"), gap),
            CanExpand = false,
            IsExpanded = false,
            ToggleCommand = null
        });
    }

    private static ChangesCodeRowViewModel CreateCodeRow(string path, DiffReviewLine line)
    {
        var kind = line.Kind switch
        {
            DiffLineKind.Added => "added",
            DiffLineKind.Removed => "removed",
            _ => "context"
        };
        return new ChangesCodeRowViewModel
        {
            LineKind = kind,
            LineNumber = line.LineNumber?.ToString() ?? string.Empty,
            Tokens = DiffLineSyntax.Tokenize(path, line.Text)
        };
    }

    private static string FoldKey(string path, DiffReviewLine line)
    {
        var anchor = line.HiddenLines is { Count: > 0 } ? line.HiddenLines[0].LineNumber ?? 0 : 0;
        return string.Concat(path, "\u001f", anchor.ToString(), "\u001f", (line.CollapsedCount ?? 0).ToString());
    }

    private void PruneState(IReadOnlyList<ModifiedFileViewModel> files)
    {
        var paths = new HashSet<string>(
            files.Select(file => file.RelativePath),
            StringComparer.OrdinalIgnoreCase);
        _collapsedFiles.RemoveWhere(path => !paths.Contains(path));
        _expandedFolds.RemoveWhere(key =>
        {
            var split = key.IndexOf('\u001f');
            var path = split < 0 ? key : key[..split];
            return !paths.Contains(path);
        });
    }

    private static int? TryCountWorkspaceLines(string? root, WorkspaceKind kind, string relativePath)
    {
        if (kind != WorkspaceKind.Local
            || string.IsNullOrWhiteSpace(root)
            || string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathRooted(relativePath))
        {
            return null;
        }

        try
        {
            var rootFull = Path.GetFullPath(root);
            var combined = Path.GetFullPath(Path.Combine(
                rootFull,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
            var rootPrefix = rootFull.EndsWith(Path.DirectorySeparatorChar)
                ? rootFull
                : rootFull + Path.DirectorySeparatorChar;
            if (!combined.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (!File.Exists(combined))
            {
                return null;
            }

            var count = 0;
            using var reader = new StreamReader(combined);
            while (reader.ReadLine() is not null)
            {
                count++;
            }

            return count;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}

public abstract class ChangesReviewRowViewModel
{
}

public sealed class ChangesFileHeaderRowViewModel : ChangesReviewRowViewModel
{
    public required string RelativePath { get; init; }

    public required WorkspaceFileIconKind IconKind { get; init; }

    public required string AddedText { get; init; }

    public required string RemovedText { get; init; }

    public required bool IsExpanded { get; init; }

    public required ICommand ToggleCommand { get; init; }
}

public sealed class ChangesCodeRowViewModel : ChangesReviewRowViewModel
{
    public required string LineKind { get; init; }

    public required string LineNumber { get; init; }

    public required IReadOnlyList<DiffSyntaxToken> Tokens { get; init; }
}

public sealed class ChangesFoldRowViewModel : ChangesReviewRowViewModel
{
    public required string Text { get; init; }

    public required bool CanExpand { get; init; }

    public required bool IsExpanded { get; init; }

    public ICommand? ToggleCommand { get; init; }
}

public sealed class ChangesNoteRowViewModel : ChangesReviewRowViewModel
{
    public required string Text { get; init; }
}
