namespace Athlon.Agent.App.ViewModels;

public sealed class FilesWorkspaceTabViewModel : WorkspaceTabViewModel
{
    public FilesWorkspaceTabViewModel(string title)
        : base(Guid.NewGuid(), WorkspaceTabKind.Files, title)
    {
    }
}

public sealed class SkillsWorkspaceTabViewModel : WorkspaceTabViewModel
{
    public SkillsWorkspaceTabViewModel(string title)
        : base(Guid.NewGuid(), WorkspaceTabKind.Skills, title)
    {
    }
}

public sealed class TerminalWorkspaceTabViewModel : WorkspaceTabViewModel
{
    public TerminalWorkspaceTabViewModel(string title)
        : base(Guid.NewGuid(), WorkspaceTabKind.Terminal, title)
    {
    }
}
