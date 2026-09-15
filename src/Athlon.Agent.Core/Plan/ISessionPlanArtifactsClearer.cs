namespace Athlon.Agent.Core.Plan;

/// <summary>
/// Single entry point for dropping everything a session accumulated around a plan: the task list,
/// the in-memory plan run and phase, and the persisted plan artifacts.
///
/// <para>Before this existed, each caller cleared a different subset — clearing the context wiped
/// <c>tasks.json</c> but left the plan (and, once plans became file-backed, would let
/// <c>ApprovedPlanRuntimeContributor</c> re-inject it on the next session switch), while deleting a
/// conversation cleared the in-memory run but left the tasks on disk.</para>
///
/// <para>Implementations live in the App layer because the timeline plan card is a UI concern.</para>
/// </summary>
public interface ISessionPlanArtifactsClearer
{
    /// <summary>
    /// Clears the session's task list, in-memory plan run/phase, persisted plan artifacts, the
    /// timeline plan card, and the auto-continuation budget. Safe to call when nothing exists.
    /// </summary>
    /// <param name="sessionId">Session whose plan artifacts should be dropped.</param>
    /// <param name="resetAutoContinue">
    /// Also reset the auto-continuation budget. Pass true when the plan is finished or abandoned;
    /// false when only the visible task list is being reset.
    /// </param>
    Task ClearAsync(string sessionId, bool resetAutoContinue = true, CancellationToken cancellationToken = default);
}
