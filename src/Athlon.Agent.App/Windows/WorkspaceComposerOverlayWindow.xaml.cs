using System.ComponentModel;
using System.Windows;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.ViewModels;

namespace Athlon.Agent.App.Windows;

public partial class WorkspaceComposerOverlayWindow : Window
{
    private bool _allowClose;

    public WorkspaceComposerOverlayWindow(
        MainShellViewModel viewModel,
        ClipboardImageAttachmentReader clipboardImageReader)
    {
        InitializeComponent();
        DataContext = viewModel;
        ComposerInput.ClipboardImageReader = clipboardImageReader;
    }

    public void FocusComposer()
    {
        Activate();
        ComposerInput.FocusInput();
    }

    public void CloseFromOwner()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
        }

        base.OnClosing(e);
    }

    private void ComposerInputWrapper_OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void ComposerInputWrapper_OnDrop(object sender, DragEventArgs e)
    {
        if (DataContext is not MainShellViewModel shell
            || e.Data.GetData(DataFormats.FileDrop) is not string[] files
            || files.Length == 0)
        {
            return;
        }

        e.Handled = true;
        await shell.AddPendingFromFilePathsAsync(files).ConfigureAwait(true);
    }
}
