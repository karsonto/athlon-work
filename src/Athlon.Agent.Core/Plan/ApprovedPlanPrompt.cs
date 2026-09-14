namespace Athlon.Agent.Core.Plan;

/// <summary>
/// Builds the user message that carries an approved plan into the conversation.
///
/// The plan itself is never persisted: once the user clicks Build the markdown is composed into
/// a normal user message, appended to the session and serialized like any other turn. The marker
/// lets the UI hide that message (it is a control message, not something the user typed), following
/// the same convention as <see cref="Athlon.Agent.Core.SubAgents.SubAgentAutoContinuePrompt"/>.
/// </summary>
public static class ApprovedPlanPrompt
{
    public const string Marker = "<athlon-approved-plan />";

    public static string BuildUserMessage(string markdown) =>
        "The user approved this plan. Start implementing it now; do not wait for another user message.\n\n"
        + Marker
        + "\n\n"
        + (markdown ?? string.Empty).Trim();

    public static bool IsApprovedPlanMessage(ChatMessage message) =>
        message.Role == MessageRole.User
        && message.Content.Contains(Marker, StringComparison.Ordinal);
}
