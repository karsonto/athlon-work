using System.IO;
using Athlon.Agent.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Athlon.Agent.App.ViewModels;

public sealed partial class ModifiedFileViewModel : ObservableObject
{
    public ModifiedFileViewModel(string relativePath, string toolName, ModifiedFileStatus status)
    {
        RelativePath = ToolPathNormalizer.ForModel(relativePath);
        ToolName = toolName;
        Status = status;
        LastModifiedAt = DateTimeOffset.UtcNow;
        DisplayName = Path.GetFileName(RelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            DisplayName = RelativePath;
        }
    }

    public string RelativePath { get; }

    public string DisplayName { get; }

    public string ToolName { get; private set; }

    [ObservableProperty]
    private ModifiedFileStatus _status;

    [ObservableProperty]
    private DateTimeOffset _lastModifiedAt;

    [ObservableProperty]
    private string? _unifiedDiffText;

    [ObservableProperty]
    private int _addedCount;

    [ObservableProperty]
    private int _removedCount;

    public bool HasDiff => !string.IsNullOrWhiteSpace(UnifiedDiffText);

    public string StatusGlyph => Status switch
    {
        ModifiedFileStatus.Pending => "○",
        ModifiedFileStatus.Succeeded => "✓",
        ModifiedFileStatus.Failed => "✗",
        _ => "○"
    };

    public void SetToolName(string toolName)
    {
        if (!string.IsNullOrWhiteSpace(toolName))
        {
            ToolName = toolName;
        }
    }

    public void SetDiff(string? unifiedDiff)
    {
        if (string.IsNullOrWhiteSpace(unifiedDiff))
        {
            return;
        }

        UnifiedDiffText = unifiedDiff;
        var counts = Services.UnifiedDiffDisplayParser.CountChanges(unifiedDiff);
        AddedCount = counts.Added;
        RemovedCount = counts.Removed;
        OnPropertyChanged(nameof(HasDiff));
    }

    partial void OnStatusChanged(ModifiedFileStatus value) => OnPropertyChanged(nameof(StatusGlyph));
}
