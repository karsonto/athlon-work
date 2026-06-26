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

    public string ToolName { get; }

    [ObservableProperty]
    private ModifiedFileStatus _status;

    [ObservableProperty]
    private DateTimeOffset _lastModifiedAt;

    public string StatusGlyph => Status switch
    {
        ModifiedFileStatus.Pending => "○",
        ModifiedFileStatus.Succeeded => "✓",
        ModifiedFileStatus.Failed => "✗",
        _ => "○"
    };

    partial void OnStatusChanged(ModifiedFileStatus value) => OnPropertyChanged(nameof(StatusGlyph));
}
