using Athlon.Agent.App.Themes;



namespace Athlon.Agent.App.Services;



internal static class ThemeHtmlStyles

{

    internal sealed record MarkdownPalette(

        string TextColor,

        string LinkColor,

        string InlineCodeBackground,

        string TableBorder,

        string TableHeaderBackground,

        string BlockquoteColor,

        string BlockquoteBackground,

        string CodeBlockBorder,

        string CodeBlockBackground,

        string CodeHeaderColor,

        string CodeButtonBackground,

        string CodeButtonBorder,

        string CodeButtonColor,

        string CodePreColor);



    internal sealed record MermaidPalette(

        string PageBackground,

        string TextColor,

        string CardBackground,

        string CardBorder,

        string SubtleText,

        string ErrorBorder,

        string ErrorBackground,

        string ErrorText,

        string MermaidTheme);



    internal static MarkdownPalette GetMarkdownPalette(bool assistantTone)

    {

        var chrome = AppThemeManager.Current.Chrome;

        return new MarkdownPalette(

            TextColor: ChatMessageToneColors.GetHtmlTextColor(assistantTone),

            LinkColor: ChatMessageToneColors.GetHtmlLinkColor(assistantTone),

            InlineCodeBackground: AppThemeColor.ToHex(chrome.CodeBackgroundAlt),

            TableBorder: AppThemeColor.ToHex(chrome.TableBorder),

            TableHeaderBackground: AppThemeColor.ToHex(chrome.CodeBackgroundAlt),

            BlockquoteColor: AppThemeColor.ToHex(chrome.TextSecondary),

            BlockquoteBackground: AppThemeColor.ToRgba(chrome.PanelAlt, 0.9),

            CodeBlockBorder: AppThemeColor.ToHex(chrome.CodeBorder),

            CodeBlockBackground: AppThemeColor.ToHex(chrome.CodeBackgroundAlt),

            CodeHeaderColor: AppThemeColor.ToHex(chrome.SubtleText),

            CodeButtonBackground: AppThemeColor.ToHex(chrome.Panel),

            CodeButtonBorder: AppThemeColor.ToHex(chrome.BorderHover),

            CodeButtonColor: AppThemeColor.ToHex(chrome.Text),

            CodePreColor: AppThemeColor.ToHex(chrome.CodeForeground));

    }



    internal static MermaidPalette GetMermaidPalette()

    {

        var chrome = AppThemeManager.Current.Chrome;

        return new MermaidPalette(

            PageBackground: AppThemeColor.ToHex(chrome.AppBackground),

            TextColor: AppThemeColor.ToHex(chrome.Text),

            CardBackground: AppThemeColor.ToHex(chrome.Panel),

            CardBorder: AppThemeColor.ToHex(chrome.Border),

            SubtleText: AppThemeColor.ToHex(chrome.SubtleText),

            ErrorBorder: AppThemeColor.ToHex(chrome.Danger),

            ErrorBackground: AppThemeColor.ToHex(chrome.ToolFailureBg),

            ErrorText: AppThemeColor.ToHex(chrome.ToolFailureText),

            MermaidTheme: AppThemeManager.CurrentKind == AppThemeKind.Light ? "default" : "dark");

    }

}

