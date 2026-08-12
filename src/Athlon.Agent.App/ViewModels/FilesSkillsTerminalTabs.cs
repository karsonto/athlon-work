using Athlon.Agent.App.Services;
using Athlon.Agent.App.Services.Terminal;
using EasyWindowsTerminalControl;

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

public sealed class TerminalWorkspaceTabViewModel : WorkspaceTabViewModel, IDisposable
{
    public TerminalWorkspaceTabViewModel(string title, string? workingDirectory = null)
        : base(Guid.NewGuid(), WorkspaceTabKind.Terminal, title)
    {
        WorkingDirectory = workingDirectory;
    }

    public string StartupCommandLine { get; set; } = string.Empty;

    public string? WorkingDirectory { get; set; }

    internal TermPTY? Session { get; set; }

    internal TerminalOutputBuffer OutputBuffer { get; } = new();

    public bool IsDisposed { get; private set; }

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        WorkspaceTerminalBootstrap.DisposeSession(Session);
        Session = null;
    }
}
