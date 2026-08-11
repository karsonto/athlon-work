using System.Windows;
using Athlon.Agent.App.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Athlon.Agent.App.Views;

public partial class WorkspacePaneView : System.Windows.Controls.UserControl
{
    public WorkspacePaneView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public void FocusFollowUpComposer()
    {
        if (!IsLoaded)
        {
            return;
        }

        FollowUpComposerInput.FocusInput();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App { Services: { } services })
        {
            FollowUpComposerInput.ClipboardImageReader =
                services.GetService<ClipboardImageAttachmentReader>();
        }
    }
}
