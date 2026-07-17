using System.Windows;

namespace Athlon.Agent.App.Windows;

public partial class WorkspaceKindPickerWindow : Window
{
    public WorkspaceKindPickerWindow()
    {
        InitializeComponent();
    }

    public bool? ChoseSsh { get; private set; }

    private void LocalButton_Click(object sender, RoutedEventArgs e)
    {
        ChoseSsh = false;
        DialogResult = true;
    }

    private void SshButton_Click(object sender, RoutedEventArgs e)
    {
        ChoseSsh = true;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        ChoseSsh = null;
        DialogResult = false;
    }
}
