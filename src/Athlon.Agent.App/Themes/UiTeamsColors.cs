using System.Windows.Media;

namespace Athlon.Agent.App.Themes;

/// <summary>
/// Agent Teams page semantic colors (aligned with <c>design/agent-teams/teams-config.html</c>).
/// Keys map to <c>Brush.Teams.*</c> resources.
/// </summary>
public sealed class UiTeamsColors
{
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
    public required Color AvatarWorkerStart { get; init; }
    public required Color AvatarWorkerEnd { get; init; }
    public required Color AvatarLeaderStart { get; init; }
    public required Color AvatarLeaderEnd { get; init; }
    public required Color OrgPreviewStart { get; init; }
    public required Color OrgPreviewMid { get; init; }
    public required Color OrgPreviewEnd { get; init; }
}
