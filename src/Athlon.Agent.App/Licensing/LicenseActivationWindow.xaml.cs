using System.IO;
using System.Windows;
using System.Windows.Input;
using Athlon.Agent.Core.Licensing;
using Microsoft.Win32;

namespace Athlon.Agent.App.Licensing;

public partial class LicenseActivationWindow : Window
{
    private readonly ILicenseValidator _validator;
    private readonly ILicenseStore _store;
    private readonly AdAccountInfo _account;

    public LicenseActivationWindow(
        ILicenseValidator validator,
        ILicenseStore store,
        AdAccountInfo account,
        LicenseValidationResult initialFailure)
    {
        InitializeComponent();
        _validator = validator;
        _store = store;
        _account = account;

        StateChanged += (_, _) => UpdateMaximizeRestoreButton();
        UpdateMaximizeRestoreButton();

        ApplyFailure(initialFailure);
        SamAccountText.Text = $"Sam：{_account.SamAccountName}";
        UpnAccountText.Text = string.IsNullOrWhiteSpace(_account.UserPrincipalName)
            ? "UPN：(无)"
            : $"UPN：{_account.UserPrincipalName}";
    }

    private void ApplyFailure(LicenseValidationResult failure)
    {
        var headline = LicenseFailureMessages.Describe(failure.FailureCode);
        var detail = failure.Message?.Trim();

        ReasonText.Text = ShouldAppendDetail(failure.FailureCode, headline, detail)
            ? $"{headline}{Environment.NewLine}{detail}"
            : headline;

        ShowInlineError(null);
    }

    private static bool ShouldAppendDetail(
        LicenseFailureCode code,
        string headline,
        string? detail)
    {
        if (code == LicenseFailureCode.Missing
            || string.IsNullOrEmpty(detail)
            || string.Equals(detail, headline, StringComparison.Ordinal))
        {
            return false;
        }

        var core = detail.TrimEnd('。');
        return string.IsNullOrEmpty(core)
            || !headline.Contains(core, StringComparison.Ordinal);
    }

    private void ShowInlineError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            InlineErrorText.Visibility = Visibility.Collapsed;
            InlineErrorText.Text = string.Empty;
            return;
        }

        InlineErrorText.Visibility = Visibility.Visible;
        InlineErrorText.Text = message;
    }

    private void OkButton_OnClick(object sender, RoutedEventArgs e)
    {
        var content = LicenseTextBox.Text;
        var result = _validator.ValidateContent(content);
        if (!result.IsValid)
        {
            ApplyFailure(result);
            return;
        }

        try
        {
            _store.SaveToUserConfig(content);
        }
        catch (Exception ex)
        {
            ShowInlineError($"保存 License 失败：{ex.Message}");
            return;
        }

        DialogResult = true;
        Close();
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleWindowState();
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void MinimizeButton_OnClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void MaximizeRestoreButton_OnClick(object sender, RoutedEventArgs e) =>
        ToggleWindowState();

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) =>
        CancelButton_OnClick(sender, e);

    private void ToggleWindowState() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void UpdateMaximizeRestoreButton()
    {
        if (MaximizeRestoreButton is null)
        {
            return;
        }

        MaximizeRestoreButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
        MaximizeRestoreButton.ToolTip = WindowState == WindowState.Maximized ? "还原" : "最大化";
    }

    private void BrowseButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 License 文件",
            Filter = "License 文件 (*.lic)|*.lic|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            LicenseTextBox.Text = File.ReadAllText(dialog.FileName);
            ShowInlineError(null);
        }
        catch (Exception ex)
        {
            ShowInlineError($"读取文件失败：{ex.Message}");
        }
    }

    private void ClearButton_OnClick(object sender, RoutedEventArgs e)
    {
        LicenseTextBox.Clear();
        ShowInlineError(null);
    }
}
