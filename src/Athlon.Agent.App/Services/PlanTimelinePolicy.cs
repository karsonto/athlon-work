namespace Athlon.Agent.App.Services;

/// <summary>
/// The plan tools that render as dedicated timeline cards instead of tool cards.
///
/// <c>publish_plan</c> is not a tool the timeline should show as a collapsible call: it publishes
/// the session plan, ends the turn, and surfaces a plan-ready card. It therefore does not fold
/// into the turn-activity summary either, and the transcript projector skips it while collecting
/// activity so its presence cannot flip a turn into "has activity" (which would fold the final
/// assistant reply away).
/// </summary>
internal static class PlanTimelinePolicy
{
    public const string PublishPlanToolName = "publish_plan";

    public static bool IsPublishPlanTool(string? toolName) =>
        string.Equals(toolName, PublishPlanToolName, StringComparison.OrdinalIgnoreCase);
}
