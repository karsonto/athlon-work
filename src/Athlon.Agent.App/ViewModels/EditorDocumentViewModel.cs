using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Athlon.Agent.App.ViewModels;

public sealed partial class EditorDocumentViewModel : ObservableObject
{
    private string _content = string.Empty;
    private string _savedContent = string.Empty;

    public EditorDocumentViewModel(string filePath, string content, string? relativePath, bool isReadOnly = false)
    {
        FilePath = filePath;
        RelativePath = relativePath;
        DisplayName = Path.GetFileName(filePath);
        TabTitle = DisplayName;
        _content = content;
        _savedContent = content;
        _isReadOnly = isReadOnly;
    }

    public string FilePath { get; }
    public string? RelativePath { get; }
    public string DisplayName { get; }
    public string TabTitle { get; private set; }

    public string Content
    {
        get => _content;
        set
        {
            if (SetProperty(ref _content, value))
            {
                IsDirty = !string.Equals(_content, _savedContent, StringComparison.Ordinal);
                UpdateTabTitle();
            }
        }
    }

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private bool _isReadOnly;

    public void MarkSaved(string content)
    {
        _savedContent = content;
        if (!SetProperty(ref _content, content, nameof(Content)))
        {
            OnPropertyChanged(nameof(Content));
        }

        IsDirty = false;
        UpdateTabTitle();
    }

    public void ReloadFromDisk(string content) => MarkSaved(content);

    private void UpdateTabTitle() => TabTitle = IsDirty ? $"{DisplayName} ●" : DisplayName;
}
