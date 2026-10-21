using System.Collections.ObjectModel;
using Athlon.Agent.App.Resources;
using Athlon.Agent.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Athlon.Agent.App.ViewModels;

public sealed partial class ChangesWorkspaceTabViewModel : WorkspaceTabViewModel
{
    public ChangesWorkspaceTabViewModel(string title)
        : base(Guid.NewGuid(), WorkspaceTabKind.Changes, title)
    {
        Files = new ObservableCollection<ModifiedFileViewModel>();
        DiffLines = new ObservableCollection<ChangesDiffLineViewModel>();
    }

    public ObservableCollection<ModifiedFileViewModel> Files { get; }

    public ObservableCollection<ChangesDiffLineViewModel> DiffLines { get; }

    [ObservableProperty]
    private ModifiedFileViewModel? _selectedFile;

    [ObservableProperty]
    private string _headline = string.Empty;

    [ObservableProperty]
    private string _addedText = string.Empty;

    [ObservableProperty]
    private string _removedText = string.Empty;

    [ObservableProperty]
    private bool _showNoDiff;

    public string NoDiffText => Strings.Get("Chat_NoDiffAvailable");

    /// <summary>Card id (<c>files:{anchor}</c>) this pane is showing, so later edits in that turn refresh it.</summary>
    public string? SourceEntryId { get; private set; }

    public void Show(IReadOnlyList<ModifiedFileViewModel> files, string? selectedPath, string? sourceEntryId = null)
    {
        if (!string.IsNullOrWhiteSpace(sourceEntryId))
        {
            SourceEntryId = sourceEntryId;
        }

        var keep = string.IsNullOrWhiteSpace(selectedPath) ? SelectedFile?.RelativePath : selectedPath;
        Files.Clear();
        foreach (var file in files)
        {
            Files.Add(file);
        }

        var match = string.IsNullOrWhiteSpace(keep)
            ? null
            : Files.FirstOrDefault(file =>
                string.Equals(file.RelativePath, keep, StringComparison.OrdinalIgnoreCase));
        SelectedFile = match ?? Files.FirstOrDefault();
        UpdateSummary();
        RebuildDiff();
    }

    partial void OnSelectedFileChanged(ModifiedFileViewModel? value)
    {
        _ = value;
        ShowNoDiff = SelectedFile is not null && !SelectedFile.HasDiff;
        RebuildDiff();
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

    private void RebuildDiff()
    {
        DiffLines.Clear();
        ShowNoDiff = SelectedFile is not null && !SelectedFile.HasDiff;
        if (SelectedFile is null || !SelectedFile.HasDiff)
        {
            return;
        }

        foreach (var line in UnifiedDiffDisplayParser.Parse(SelectedFile.UnifiedDiffText, foldContext: true))
        {
            DiffLines.Add(ChangesDiffLineViewModel.From(line));
        }
    }
}

public sealed class ChangesDiffLineViewModel
{
    public required string Kind { get; init; }

    public required string Text { get; init; }

    public required string OldNumber { get; init; }

    public required string NewNumber { get; init; }

    public required string Prefix { get; init; }

    public static ChangesDiffLineViewModel From(DiffDisplayLine line)
    {
        var kind = line.Kind switch
        {
            DiffLineKind.Added => "added",
            DiffLineKind.Removed => "removed",
            DiffLineKind.HunkHeader => "hunk",
            DiffLineKind.Header => "header",
            DiffLineKind.Collapsed => "collapsed",
            _ => "context"
        };
        var prefix = line.Kind switch
        {
            DiffLineKind.Added => "+",
            DiffLineKind.Removed => "-",
            DiffLineKind.Collapsed => string.Empty,
            _ => " "
        };
        var text = line.Kind == DiffLineKind.Collapsed
            ? string.Format(Strings.Get("Chat_UnmodifiedLines"), line.CollapsedCount ?? 0)
            : line.Text;
        return new ChangesDiffLineViewModel
        {
            Kind = kind,
            Text = text,
            OldNumber = line.OldLine?.ToString() ?? string.Empty,
            NewNumber = line.NewLine?.ToString() ?? string.Empty,
            Prefix = prefix
        };
    }
}
