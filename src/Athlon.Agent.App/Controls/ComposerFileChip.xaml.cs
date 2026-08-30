using System.Windows;
using System.Windows.Controls;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.ViewModels;

namespace Athlon.Agent.App.Controls;

public partial class ComposerFileChip : UserControl
{
    public static readonly DependencyProperty InsertTextProperty = DependencyProperty.Register(
        nameof(InsertText),
        typeof(string),
        typeof(ComposerFileChip),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty FileNameProperty = DependencyProperty.Register(
        nameof(FileName),
        typeof(string),
        typeof(ComposerFileChip),
        new PropertyMetadata(string.Empty, OnFileNameChanged));

    public static readonly DependencyProperty IconKindProperty = DependencyProperty.Register(
        nameof(IconKind),
        typeof(WorkspaceFileIconKind),
        typeof(ComposerFileChip),
        new PropertyMetadata(WorkspaceFileIconKind.File, OnIconKindChanged));

    public static readonly DependencyProperty MentionKindProperty = DependencyProperty.Register(
        nameof(MentionKind),
        typeof(ComposerMentionKind),
        typeof(ComposerFileChip),
        new PropertyMetadata(ComposerMentionKind.File, OnMentionKindChanged));

    public static readonly DependencyProperty ToolTipPathProperty = DependencyProperty.Register(
        nameof(ToolTipPath),
        typeof(string),
        typeof(ComposerFileChip),
        new PropertyMetadata(string.Empty, OnToolTipPathChanged));

    public ComposerFileChip()
    {
        InitializeComponent();
        ApplyMentionKind(MentionKind);
    }

    public string InsertText
    {
        get => (string)GetValue(InsertTextProperty);
        set => SetValue(InsertTextProperty, value);
    }

    public string FileName
    {
        get => (string)GetValue(FileNameProperty);
        set => SetValue(FileNameProperty, value);
    }

    public WorkspaceFileIconKind IconKind
    {
        get => (WorkspaceFileIconKind)GetValue(IconKindProperty);
        set => SetValue(IconKindProperty, value);
    }

    public ComposerMentionKind MentionKind
    {
        get => (ComposerMentionKind)GetValue(MentionKindProperty);
        set => SetValue(MentionKindProperty, value);
    }

    public string ToolTipPath
    {
        get => (string)GetValue(ToolTipPathProperty);
        set => SetValue(ToolTipPathProperty, value);
    }

    private static void OnFileNameChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ComposerFileChip chip && chip.FileNameText is not null)
        {
            chip.FileNameText.Text = e.NewValue as string ?? string.Empty;
        }
    }

    private static void OnIconKindChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ComposerFileChip chip && chip.FileIcon is not null)
        {
            chip.FileIcon.Kind = (WorkspaceFileIconKind)e.NewValue;
        }
    }

    private static void OnMentionKindChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ComposerFileChip chip)
        {
            chip.ApplyMentionKind((ComposerMentionKind)e.NewValue);
        }
    }

    private static void OnToolTipPathChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ComposerFileChip chip && chip.ChipBorder is not null)
        {
            var path = e.NewValue as string;
            chip.ChipBorder.ToolTip = string.IsNullOrWhiteSpace(path) ? null : path;
        }
    }

    private void ApplyMentionKind(ComposerMentionKind kind)
    {
        if (ChipBorder is null || FileIcon is null || TypeBadgeText is null || FileNameText is null)
        {
            return;
        }

        var isFile = kind == ComposerMentionKind.File;
        FileIcon.Visibility = isFile ? Visibility.Visible : Visibility.Collapsed;
        TypeBadgeText.Visibility = isFile ? Visibility.Collapsed : Visibility.Visible;
        TypeBadgeText.Text = kind switch
        {
            ComposerMentionKind.Skill => "技能",
            ComposerMentionKind.Mcp => "MCP",
            _ => string.Empty
        };

        var (bg, border, text) = kind switch
        {
            ComposerMentionKind.Skill => (
                "Brush.AtCompletionSkillBadgeBg",
                "Brush.AtCompletionSkillBadgeBorder",
                "Brush.AtCompletionSkillBadgeText"),
            ComposerMentionKind.Mcp => (
                "Brush.AtCompletionMcpBadgeBg",
                "Brush.AtCompletionMcpBadgeBorder",
                "Brush.AtCompletionMcpBadgeText"),
            _ => (
                "Brush.AtCompletionFileBadgeBg",
                "Brush.AtCompletionFileBadgeBorder",
                "Brush.AtCompletionFileBadgeText")
        };

        ChipBorder.SetResourceReference(Border.BackgroundProperty, bg);
        ChipBorder.SetResourceReference(Border.BorderBrushProperty, border);
        FileNameText.SetResourceReference(TextBlock.ForegroundProperty, text);
        TypeBadgeText.SetResourceReference(TextBlock.ForegroundProperty, text);
    }
}
