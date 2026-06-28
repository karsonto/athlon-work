using System.Windows;

namespace Athlon.Agent.App.Localization;

public sealed class UserNotifier(ILocalizationService localization) : IUserNotifier
{
    public void Info(string titleKey, string messageKey, params object[] messageArgs) =>
        Show(titleKey, messageKey, MessageBoxButton.OK, MessageBoxImage.Information, messageArgs);

    public void Warning(string titleKey, string messageKey, params object[] messageArgs) =>
        Show(titleKey, messageKey, MessageBoxButton.OK, MessageBoxImage.Warning, messageArgs);

    public void InfoText(string titleKey, string messageText) =>
        MessageBox.Show(messageText, localization[titleKey], MessageBoxButton.OK, MessageBoxImage.Information);

    public void WarningText(string titleKey, string messageText) =>
        MessageBox.Show(messageText, localization[titleKey], MessageBoxButton.OK, MessageBoxImage.Warning);

    public bool Confirm(string titleKey, string messageKey, params object[] messageArgs) =>
        Show(titleKey, messageKey, MessageBoxButton.OKCancel, MessageBoxImage.Question, messageArgs)
        == MessageBoxResult.OK;

    public bool ConfirmYesNo(string titleKey, string messageKey, params object[] messageArgs) =>
        Show(titleKey, messageKey, MessageBoxButton.YesNo, MessageBoxImage.Warning, messageArgs)
        == MessageBoxResult.Yes;

    public MessageBoxResult AskYesNoCancel(string titleKey, string messageKey, params object[] messageArgs) =>
        Show(titleKey, messageKey, MessageBoxButton.YesNoCancel, MessageBoxImage.Question, messageArgs);

    private MessageBoxResult Show(
        string titleKey,
        string messageKey,
        MessageBoxButton button,
        MessageBoxImage image,
        object[] messageArgs)
    {
        var title = localization[titleKey];
        var message = messageArgs.Length == 0
            ? localization[messageKey]
            : localization.Format(messageKey, messageArgs);
        return MessageBox.Show(message, title, button, image);
    }
}
