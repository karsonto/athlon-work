using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Athlon.Agent.App.Services;
using Microsoft.Xaml.Behaviors;

namespace Athlon.Agent.App.Behaviors;

public enum LayoutSplitterKind
{
    NavigationSidebar,
    EditorPane,
    Composer,
    ContextSidebar
}

public sealed class GridSplitterLayoutBehavior : Behavior<GridSplitter>
{
    public static readonly DependencyProperty KindProperty =
        DependencyProperty.Register(
            nameof(Kind),
            typeof(LayoutSplitterKind),
            typeof(GridSplitterLayoutBehavior),
            new PropertyMetadata(LayoutSplitterKind.NavigationSidebar));

    public LayoutSplitterKind Kind
    {
        get => (LayoutSplitterKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    protected override void OnAttached()
    {
        base.OnAttached();
        AssociatedObject.DragCompleted += OnDragCompleted;
    }

    protected override void OnDetaching()
    {
        AssociatedObject.DragCompleted -= OnDragCompleted;
        base.OnDetaching();
    }

    private void OnDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (Window.GetWindow(AssociatedObject) is not IMainWindowLayoutHost host)
        {
            return;
        }

        switch (Kind)
        {
            case LayoutSplitterKind.NavigationSidebar:
                host.OnNavigationSidebarDragCompleted();
                break;
            case LayoutSplitterKind.EditorPane:
                host.OnEditorPaneDragCompleted();
                break;
            case LayoutSplitterKind.Composer:
                host.OnComposerDragCompleted();
                break;
            case LayoutSplitterKind.ContextSidebar:
                host.OnContextSidebarDragCompleted();
                break;
        }
    }
}
