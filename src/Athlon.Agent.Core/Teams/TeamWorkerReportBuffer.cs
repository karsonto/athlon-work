namespace Athlon.Agent.Core.Teams;

/// <summary>
/// Ambient buffer for <c>team_report_result</c> so the Worker run executor can prefer a tool-authored
/// structured result over free-text ## Result parsing.
/// </summary>
public sealed class TeamWorkerReportBuffer
{
    private static readonly AsyncLocal<TeamWorkerReportBuffer?> Ambient = new();

    public TeamWorkerReportedResult? Report { get; set; }

    public static TeamWorkerReportBuffer? Current => Ambient.Value;

    public static IDisposable Enter()
    {
        var previous = Ambient.Value;
        var created = new TeamWorkerReportBuffer();
        Ambient.Value = created;
        return new Popper(previous);
    }

    private sealed class Popper(TeamWorkerReportBuffer? previous) : IDisposable
    {
        public void Dispose() => Ambient.Value = previous;
    }
}

public sealed record TeamWorkerReportedResult(
    TeamMemberResultStatus Status,
    string? Verdict,
    string? FixTargetSubtaskId,
    string ResponseText,
    IReadOnlyList<string> ArtifactPaths);
