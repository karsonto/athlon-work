using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Athlon.Agent.App.Services;

namespace Athlon.Agent.App.Controls;

public static class DiffSyntaxText
{
    private static readonly Dictionary<Color, SolidColorBrush> Brushes = new();

    public static readonly DependencyProperty TokensProperty = DependencyProperty.RegisterAttached(
        "Tokens",
        typeof(IReadOnlyList<DiffSyntaxToken>),
        typeof(DiffSyntaxText),
        new PropertyMetadata(null, OnTokensChanged));

    public static void SetTokens(DependencyObject element, IReadOnlyList<DiffSyntaxToken>? value) =>
        element.SetValue(TokensProperty, value);

    public static IReadOnlyList<DiffSyntaxToken>? GetTokens(DependencyObject element) =>
        (IReadOnlyList<DiffSyntaxToken>?)element.GetValue(TokensProperty);

    private static void OnTokensChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block)
        {
            return;
        }

        block.Inlines.Clear();
        if (e.NewValue is not IReadOnlyList<DiffSyntaxToken> tokens)
        {
            return;
        }

        foreach (var token in tokens)
        {
            var run = new Run(token.Text);
            if (token.Foreground is Color color)
            {
                run.Foreground = BrushFor(color);
            }

            block.Inlines.Add(run);
        }
    }

    private static SolidColorBrush BrushFor(Color color)
    {
        if (!Brushes.TryGetValue(color, out var brush))
        {
            brush = new SolidColorBrush(color);
            brush.Freeze();
            Brushes[color] = brush;
        }

        return brush;
    }
}
