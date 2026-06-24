using System.Windows;
using System.Windows.Controls;

namespace Athlon.Agent.App.Behaviors;

public static class PasswordBoxBindingBehavior
{
    private static readonly DependencyProperty IsUpdatingProperty =
        DependencyProperty.RegisterAttached(
            "IsUpdating",
            typeof(bool),
            typeof(PasswordBoxBindingBehavior),
            new PropertyMetadata(false));

    public static readonly DependencyProperty BoundPasswordProperty =
        DependencyProperty.RegisterAttached(
            "BoundPassword",
            typeof(string),
            typeof(PasswordBoxBindingBehavior),
            new FrameworkPropertyMetadata(
                string.Empty,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnBoundPasswordChanged));

    public static string GetBoundPassword(DependencyObject obj) =>
        (string)obj.GetValue(BoundPasswordProperty);

    public static void SetBoundPassword(DependencyObject obj, string value) =>
        obj.SetValue(BoundPasswordProperty, value);

    private static void OnBoundPasswordChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not PasswordBox passwordBox)
        {
            return;
        }

        passwordBox.PasswordChanged -= PasswordBox_OnPasswordChanged;

        if (!(bool)passwordBox.GetValue(IsUpdatingProperty))
        {
            passwordBox.Password = e.NewValue as string ?? string.Empty;
        }

        passwordBox.PasswordChanged += PasswordBox_OnPasswordChanged;
    }

    private static void PasswordBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not PasswordBox passwordBox)
        {
            return;
        }

        passwordBox.SetValue(IsUpdatingProperty, true);
        SetBoundPassword(passwordBox, passwordBox.Password);
        passwordBox.SetValue(IsUpdatingProperty, false);
    }
}
