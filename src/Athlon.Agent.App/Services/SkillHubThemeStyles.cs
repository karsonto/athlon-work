using System.Text;
using System.Text.Json;
using Athlon.Agent.App.Themes;

namespace Athlon.Agent.App.Services;

/// <summary>Builds CSS theme tokens for the Skill Hub WebView from <see cref="AppThemeManager"/>.</summary>
internal static class SkillHubThemeStyles
{
    public static string GetThemeTokenStyles()
    {
        var isLight = AppThemeManager.CurrentKind == AppThemeKind.Light;
        var chrome = AppThemeManager.Current.Chrome;
        var accentFg = isLight ? "#FFFFFF" : AppThemeColor.ToHex(chrome.Text);
        var iconBg = AppThemeColor.ToHex(isLight ? chrome.PanelAlt : chrome.SurfaceHover);
        var badgeBg = AppThemeColor.ToHex(isLight ? chrome.AccentSubtle : chrome.PanelAlt);

        return $$"""
            :root {
              --hub-bg: {{AppThemeColor.ToHex(chrome.ChatBackgroundTop)}};
              --hub-panel: {{AppThemeColor.ToHex(chrome.Panel)}};
              --hub-panel-hover: {{AppThemeColor.ToHex(chrome.SurfaceHover)}};
              --hub-border: {{AppThemeColor.ToHex(chrome.Border)}};
              --hub-text: {{AppThemeColor.ToHex(chrome.Text)}};
              --hub-muted: {{AppThemeColor.ToHex(chrome.SubtleText)}};
              --hub-accent: {{AppThemeColor.ToHex(chrome.Accent)}};
              --hub-accent-hover: {{AppThemeColor.ToHex(chrome.AccentHover)}};
              --hub-accent-fg: {{accentFg}};
              --hub-btn: {{AppThemeColor.ToHex(chrome.AccentSubtle)}};
              --hub-btn-hover: {{AppThemeColor.ToHex(chrome.AccentHover)}};
              --hub-btn-fg: {{AppThemeColor.ToHex(isLight ? chrome.Accent : chrome.Text)}};
              --hub-icon-bg: {{iconBg}};
              --hub-badge-bg: {{badgeBg}};
            }
            """;
    }

    public static string BuildThemeUpdateScript()
    {
        var tokensB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(GetThemeTokenStyles()));
        return
            "if(typeof applyThemeUpdate==='function')applyThemeUpdate(" +
            JsonSerializer.Serialize(tokensB64) +
            ");";
    }
}
