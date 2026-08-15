using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Athlon.Agent.App.Resources;

namespace Athlon.Agent.App.Windows;

/// <summary>App-themed modal for info / warning / confirm prompts (replaces system MessageBox via IUserNotifier).</summary>
public partial class ThemedMessageWindow : Window
{
    private MessageBoxResult _result = MessageBoxResult.None;

    public ThemedMessageWindow()
    {
        InitializeComponent();
    }

    public static MessageBoxResult Show(
        Window? owner,
        string title,
        string message,
        MessageBoxButton buttons,
        MessageBoxImage image)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            return dispatcher.Invoke(() => Show(owner, title, message, buttons, image));
        }

        var dialog = new ThemedMessageWindow();
        dialog.Configure(title, message, buttons, image);

        if (owner is { IsLoaded: true })
        {
            dialog.Owner = owner;
        }
        else if (Application.Current?.MainWindow is { IsLoaded: true } main)
        {
            dialog.Owner = main;
        }

        if (dialog.Owner is null)
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        dialog.ShowDialog();
        return dialog._result;
    }

    private void Configure(string title, string message, MessageBoxButton buttons, MessageBoxImage image)
    {
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        ApplyKindAccent(image);
        BuildButtons(buttons);
    }

    private void ApplyKindAccent(MessageBoxImage image)
    {
        var brushKey = image switch
        {
            MessageBoxImage.Warning or MessageBoxImage.Exclamation => "Brush.Warning",
            MessageBoxImage.Error or MessageBoxImage.Hand or MessageBoxImage.Stop => "Brush.Danger",
            MessageBoxImage.Question => "Brush.Accent",
            _ => "Brush.Accent"
        };

        if (TryFindResource(brushKey) is Brush brush)
        {
            KindDot.Fill = brush;
        }
    }

    private void BuildButtons(MessageBoxButton buttons)
    {
        ButtonPanel.Children.Clear();

        switch (buttons)
        {
            case MessageBoxButton.OK:
                AddButton(Strings.Get("Common_OK"), MessageBoxResult.OK, isDefault: true, isCancel: true);
                break;
            case MessageBoxButton.OKCancel:
                AddButton(Strings.Get("Common_Cancel"), MessageBoxResult.Cancel, isCancel: true);
                AddButton(Strings.Get("Common_OK"), MessageBoxResult.OK, isDefault: true, emphasize: true);
                break;
            case MessageBoxButton.YesNo:
                AddButton(Strings.Get("Common_No"), MessageBoxResult.No, isCancel: true);
                AddButton(Strings.Get("Common_Yes"), MessageBoxResult.Yes, isDefault: true, emphasize: true);
                break;
            case MessageBoxButton.YesNoCancel:
                AddButton(Strings.Get("Common_Cancel"), MessageBoxResult.Cancel, isCancel: true);
                AddButton(Strings.Get("Common_No"), MessageBoxResult.No);
                AddButton(Strings.Get("Common_Yes"), MessageBoxResult.Yes, isDefault: true, emphasize: true);
                break;
            default:
                AddButton(Strings.Get("Common_OK"), MessageBoxResult.OK, isDefault: true, isCancel: true);
                break;
        }
    }

    private void AddButton(
        string content,
        MessageBoxResult result,
        bool isDefault = false,
        bool isCancel = false,
        bool emphasize = false)
    {
        var button = new Button
        {
            Content = content,
            MinWidth = 88,
            Margin = new Thickness(8, 0, 0, 0),
            IsDefault = isDefault,
            IsCancel = isCancel,
            Style = TryFindResource("GhostButtonStyle") as Style
        };

        if (emphasize)
        {
            button.Foreground = TryFindResource("Brush.Text") as Brush ?? button.Foreground;
            button.BorderBrush = TryFindResource("Brush.Accent") as Brush ?? button.BorderBrush;
        }

        button.Click += (_, _) =>
        {
            _result = result;
            DialogResult = result is MessageBoxResult.OK or MessageBoxResult.Yes;
            Close();
        };

        ButtonPanel.Children.Add(button);
    }

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_result == MessageBoxResult.None)
        {
            _result = MessageBoxResult.Cancel;
        }

        Close();
    }
}
