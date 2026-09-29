namespace Athlon.Agent.Core.Plan;

/// <summary>
/// Hidden user message that asks the model to call <c>publish_plan</c> after it wrote a plan in prose.
/// </summary>
public static class PlanPublishRepairPrompt
{
    public const string Marker = "<athlon-plan-publish-repair />";

    public static string Build(string prose) =>
        "The previous reply contains a plan but publish_plan was not called. "
        + "Call publish_plan now with that plan (title, overview, ## Steps, ## Acceptance). "
        + "Do not explore the workspace, edit files, or run shell.\n\n"
        + Marker
        + "\n\n"
        + (prose ?? string.Empty).Trim();

    public static bool IsRepairMessage(ChatMessage message) =>
        message.Role == MessageRole.User
        && message.Content.Contains(Marker, StringComparison.Ordinal);

    /// <summary>
    /// Latest assistant reply that already contains a plan outline and still needs <c>publish_plan</c>.
    /// </summary>
    public static string? TryExtractProse(AgentSession session)
    {
        for (var index = session.Messages.Count - 1; index >= 0; index--)
        {
            var message = session.Messages[index];
            if (message.Role != MessageRole.Assistant || string.IsNullOrWhiteSpace(message.Content))
            {
                continue;
            }

            var content = message.Content;
            if (content.Contains("## Steps", StringComparison.OrdinalIgnoreCase)
                && content.Contains("## Acceptance", StringComparison.OrdinalIgnoreCase))
            {
                return content;
            }

            return null;
        }

        return null;
    }
}
