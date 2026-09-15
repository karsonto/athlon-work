using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.Plan;

namespace Athlon.Agent.App.Services;

/// <summary>
/// App-layer implementation of <see cref="ISessionPlanArtifactsClearer"/>.
///
/// <para>Touches every place a plan leaves a trace: the in-memory run and phase, the plan card on
/// the timeline, the persisted <c>plan.md</c>/<c>plan.json</c>, the persisted <c>tasks.json</c>,
/// and the auto-continuation budget. Callers previously each cleared a different subset.</para>
/// </summary>
public sealed class SessionPlanArtifactsClearer(
    IPlanPhaseAccessor planPhaseAccessor,
    IPlanRunStore planRunStore,
    IPlanArtifactStore planArtifactStore,
    ISessionTaskListStore taskListStore,
    ITaskListChangedNotifier taskListChangedNotifier,
    IPlanContinuationTracker continuationTracker,
    PlanActionBarViewModel planBar,
    IAppLogger logger) : ISessionPlanArtifactsClearer
{
    private readonly IAppLogger _logger = logger.ForContext("SessionPlanArtifactsClearer");

    public async Task ClearAsync(
        string sessionId,
        bool resetAutoContinue = true,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        try
        {
            // Order matters: drop the in-memory run first so a concurrent refresh cannot
            // re-hydrate the plan from the store while we are deleting the artifacts.
            planPhaseAccessor.Clear(sessionId);
            await planRunStore.ClearActiveAsync(sessionId, cancellationToken).ConfigureAwait(true);
            await planArtifactStore.ClearAsync(sessionId, cancellationToken).ConfigureAwait(true);
            await taskListStore
                .ReplaceAsync(sessionId, new SessionTaskList(), cancellationToken)
                .ConfigureAwait(true);

            if (resetAutoContinue)
            {
                continuationTracker.Reset(sessionId);
            }

            // Refresh the composer task panel and drop the timeline card for the displayed session.
            taskListChangedNotifier.Notify(sessionId);
            planBar.NotifyRunCleared(sessionId);
        }
        catch (Exception ex)
        {
            _logger.Warning("Failed to clear plan artifacts for session {SessionId}: {Error}", sessionId, ex.Message);
        }
    }
}
