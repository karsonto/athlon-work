using System.Windows;
using System.Windows.Input;

namespace Athlon.Agent.App.Windows;

public partial class PlanDocumentWindow : Window
{
    public PlanDocumentWindow()
    {
        InitializeComponent();
    }

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();
}
