using System.Windows;

namespace Athlon.Agent.App.Localization;

public interface IUserNotifier
{
    void Info(string titleKey, string messageKey, params object[] messageArgs);

    void Warning(string titleKey, string messageKey, params object[] messageArgs);

    void InfoText(string titleKey, string messageText);

    void WarningText(string titleKey, string messageText);

    bool Confirm(string titleKey, string messageKey, params object[] messageArgs);

    bool ConfirmYesNo(string titleKey, string messageKey, params object[] messageArgs);

    MessageBoxResult AskYesNoCancel(string titleKey, string messageKey, params object[] messageArgs);
}
