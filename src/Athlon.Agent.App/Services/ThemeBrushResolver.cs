using System.Windows.Media;
using Athlon.Agent.App.Themes;

namespace Athlon.Agent.App.Services;

/// <summary>Resolves theme brushes from application resources with palette fallbacks.</summary>
public static class ThemeBrushResolver
{
    public static Brush Get(string key)
    {
        if (FlowDocumentThemeNormalizer.ResolveBrush(key) is Brush brush)
        {
            return brush;
        }

        var chrome = AppThemeManager.Current.Chrome;
        var color = key switch
        {
            "Brush.Text" => chrome.Text,
            "Brush.TextSecondary" => chrome.TextSecondary,
            "Brush.SubtleText" => chrome.SubtleText,
            "Brush.CodeBackground" => chrome.CodeBackground,
            "Brush.CodeBackgroundAlt" => chrome.CodeBackgroundAlt,
            "Brush.CodeForeground" => chrome.CodeForeground,
            "Brush.CodeBorder" => chrome.CodeBorder,
            "Brush.CodeHighlightBlue" => chrome.CodeHighlightBlue,
            "Brush.TableBorder" => chrome.TableBorder,
            "Brush.Border" => chrome.Border,
            "Brush.Panel" => chrome.Panel,
            "Brush.UserBubble" => chrome.UserBubble,
            _ => chrome.Text,
        };

        return AppThemeColor.ToFrozenBrush(color);
    }
}
