using CommunityToolkit.Mvvm.ComponentModel;

namespace Athlon.Agent.App.ViewModels;

public abstract partial class WorkspaceTabViewModel : ObservableObject
{
    protected WorkspaceTabViewModel(Guid id, WorkspaceTabKind kind, string title, bool canClose = true)
    {
        Id = id;
        Kind = kind;
        Title = title;
        CanClose = canClose;
    }

    public Guid Id { get; }

    public WorkspaceTabKind Kind { get; }

    [ObservableProperty]
    private string title;

    public bool CanClose { get; }
}
