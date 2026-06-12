using System.Windows.Media;

namespace Athlon.Agent.App.Themes;

/// <summary>Shared foreground/link colors for assistant vs user chat messages.</summary>
public static class ChatMessageToneColors
{
    private static readonly Color UserBubbleText = Colors.White;
    private static readonly Color UserBubbleLink = Color.FromRgb(0xDB, 0xEA, 0xFE);

    public static Color GetMessageTextColor(bool assistantTone, UiChromeColors? chrome = null)
    {
        chrome ??= AppThemeManager.Current.Chrome;
        return assistantTone ? chrome.Text : UserBubbleText;
    }

    public static Brush GetMessageTextBrush(bool assistantTone) =>
        AppThemeColor.ToFrozenBrush(GetMessageTextColor(assistantTone));

    public static string GetHtmlTextColor(bool assistantTone) =>
        AppThemeColor.ToHex(GetMessageTextColor(assistantTone));

    public static string GetHtmlLinkColor(bool assistantTone)
    {
        if (!assistantTone)
        {
            return AppThemeColor.ToHex(UserBubbleLink);
        }

        var chrome = AppThemeManager.Current.Chrome;
        return AppThemeColor.ToHex(chrome.CodeHighlightBlue);
    }
}
