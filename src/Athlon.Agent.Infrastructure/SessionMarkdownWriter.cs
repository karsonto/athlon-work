using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure;

public static class SessionMarkdownWriter
{
    public static string WriteConversation(AgentSession session)
    {
        var lines = new List<string>
        {
            $"# {session.Title}",
            "",
            $"- Session: `{session.Id}`",
            $"- Created: `{session.CreatedAt:u}`",
            $"- Updated: `{session.UpdatedAt:u}`",
            ""
        };

        foreach (var message in session.Messages)
        {
            var roleLabel = message.Role == MessageRole.Compaction ? "Compaction" : message.Role.ToString();
            lines.Add($"## {roleLabel} - {message.CreatedAt:u}");
            lines.Add("");
            if (message.ImageAttachments is { Count: > 0 })
            {
                lines.Add($"附图: {string.Join(", ", message.ImageAttachments.Select(image => image.FileName))}");
                lines.Add("");
            }
            lines.Add(message.Content);
            lines.Add("");
        }

        return string.Join(Environment.NewLine, lines);
    }

    public static string WriteSummary(ContextSummary summary) =>
        $"# Context Summary\n\n- Session: `{summary.SessionId}`\n- Created: `{summary.CreatedAt:u}`\n- Original messages: `{summary.OriginalMessageCount}`\n\n{summary.Content}\n";
}
