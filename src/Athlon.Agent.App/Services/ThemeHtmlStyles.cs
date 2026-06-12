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
        if (AppThemeManager.CurrentKind == AppThemeKind.Light)
        {
            return new MarkdownPalette(
                TextColor: assistantTone ? ReportHtmlLightColors.Slate900 : "#FFFFFF",
                LinkColor: assistantTone ? ReportHtmlLightColors.Sky600 : "#DBEAFE",
                InlineCodeBackground: ReportHtmlLightColors.Slate200,
                TableBorder: ReportHtmlLightColors.Slate300,
                TableHeaderBackground: ReportHtmlLightColors.Slate100,
                BlockquoteColor: ReportHtmlLightColors.Slate600,
                BlockquoteBackground: "rgba(241, 245, 249, 0.9)",
                CodeBlockBorder: ReportHtmlLightColors.Slate200,
                CodeBlockBackground: ReportHtmlLightColors.Slate50,
                CodeHeaderColor: ReportHtmlLightColors.Slate600,
                CodeButtonBorder: ReportHtmlLightColors.Slate300,
                CodeButtonColor: ReportHtmlLightColors.Slate700,
                CodePreColor: ReportHtmlLightColors.Slate800);
        }

        return new MarkdownPalette(
            TextColor: assistantTone ? "#F4F4F5" : "#EFF6FF",
            LinkColor: assistantTone ? "#93C5FD" : "#DBEAFE",
            InlineCodeBackground: "#27272A",
            TableBorder: "#3F3F46",
            TableHeaderBackground: "#27272A",
            BlockquoteColor: "#A1A1AA",
            BlockquoteBackground: "rgba(39, 39, 42, 0.5)",
            CodeBlockBorder: "#1E293B",
            CodeBlockBackground: "#020617",
            CodeHeaderColor: "#CBD5E1",
            CodeButtonBorder: "#334155",
            CodeButtonColor: "#CBD5E1",
            CodePreColor: "#F1F5F9");
    }

    internal static MermaidPalette GetMermaidPalette()
    {
        if (AppThemeManager.CurrentKind == AppThemeKind.Light)
        {
            return new MermaidPalette(
                PageBackground: ReportHtmlLightColors.Slate100,
                TextColor: ReportHtmlLightColors.Slate900,
                CardBackground: ReportHtmlLightColors.White,
                CardBorder: ReportHtmlLightColors.Slate200,
                SubtleText: ReportHtmlLightColors.Slate500,
                ErrorBorder: ReportHtmlLightColors.Rose600,
                ErrorBackground: ReportHtmlLightColors.Rose50,
                ErrorText: ReportHtmlLightColors.Rose700,
                MermaidTheme: "default");
        }

        return new MermaidPalette(
            PageBackground: "#101012",
            TextColor: "#F4F4F5",
            CardBackground: "#18181B",
            CardBorder: "#3F3F46",
            SubtleText: "#A1A1AA",
            ErrorBorder: "#E11D48",
            ErrorBackground: "#2A1418",
            ErrorText: "#FDA4AF",
            MermaidTheme: "dark");
    }
}
