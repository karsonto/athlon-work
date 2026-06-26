using System.Windows.Media;

namespace Athlon.Agent.App.Themes;

/// <summary>Shared foreground/link colors for assistant vs user chat messages.</summary>
public static class ChatMessageToneColors
{
    public static Color GetMessageTextColor(bool assistantTone, UiChromeColors? chrome = null)
    {
        chrome ??= AppThemeManager.Current.Chrome;
        return chrome.Text;
    }

    public static Brush GetMessageTextBrush(bool assistantTone) =>
        AppThemeColor.ToFrozenBrush(GetMessageTextColor(assistantTone));

    public static string GetHtmlTextColor(bool assistantTone) =>
        AppThemeColor.ToHex(GetMessageTextColor(assistantTone));

    public static string GetHtmlLinkColor(bool assistantTone)
    {
        var chrome = AppThemeManager.Current.Chrome;
        return AppThemeColor.ToHex(chrome.CodeHighlightBlue);
    }
}
