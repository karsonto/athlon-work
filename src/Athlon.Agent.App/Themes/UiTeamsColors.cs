using System.Windows.Media;

namespace Athlon.Agent.App.Themes;

/// <summary>
/// Agent Teams page semantic colors (aligned with <c>design/agent-teams/teams-config.html</c>).
/// Keys map to <c>Brush.Teams.*</c> resources. All fills are solid — no gradients.
/// </summary>
public sealed class UiTeamsColors
{
    public required Color Accent { get; init; }
    public required Color AccentHover { get; init; }
    public required Color AccentSoft { get; init; }
    public required Color Leader { get; init; }
    public required Color LeaderSoft { get; init; }
    public required Color Worker { get; init; }
    public required Color WorkerSoft { get; init; }
    public required Color Success { get; init; }
    public required Color SuccessSoft { get; init; }
    public required Color Warn { get; init; }
    public required Color WarnSoft { get; init; }
    public required Color Error { get; init; }
    public required Color ErrorSoft { get; init; }
    public required Color ErrorBorder { get; init; }
    public required Color AvatarLeader { get; init; }
    public required Color AvatarWorker { get; init; }
    public required Color OrgPreview { get; init; }
}
