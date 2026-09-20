using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Plan;

namespace Athlon.Agent.App.Services;

/// <summary>
/// Computes a cheap, conservative fingerprint of everything that feeds
/// <see cref="ChatEventSerializer.BuildReplayEvents"/>. Two calls with the same revision are
/// guaranteed to serialize to the same replay event stream; any doubt resolves to a different
/// hash (a miss), never to a stale hit.
///
/// The fingerprint is intentionally broad: message text, role, tool status/approval/expansion
/// (expansion changes tool-result truncation), the visible plan run and the UI culture (the
/// serializer embeds localized labels).
/// </summary>
internal static class ChatReplayRevision
{
    /// <summary>
    /// Builds a stable revision string for a replay. <paramref name="activitySource"/> (when
    /// present) replaces <paramref name="messages"/> as the timeline source, mirroring
    /// <see cref="ChatEventSerializer.BuildReplayEvents"/>.
    /// </summary>
    public static string Compute(
        IReadOnlyList<ChatMessageViewModel> messages,
        bool showToolCalls,
        IReadOnlyList<ChatMessage>? activitySource,
        PlanRun? planRun)
    {
        var builder = new StringBuilder(256);
        builder.Append("v1|sc=").Append(showToolCalls ? '1' : '0');
        builder.Append("|cul=").Append(CultureInfo.CurrentUICulture.Name);
        builder.Append("|plan=").Append(DescribePlan(planRun));
        builder.Append('\n');

        if (activitySource is { Count: > 0 })
        {
            builder.Append("src=activity|n=").Append(activitySource.Count).Append('\n');
            foreach (var message in activitySource)
            {
                AppendCoreMessage(builder, message);
            }
        }
        else
        {
            builder.Append("src=messages|n=").Append(messages.Count).Append('\n');
            foreach (var message in messages)
            {
                AppendViewModelMessage(builder, message);
            }
        }

        return Hash(builder);
    }

    private static void AppendCoreMessage(StringBuilder builder, ChatMessage message)
    {
        builder.Append(message.Id).Append('\u001f')
            .Append(message.Role).Append('\u001f')
            .Append(message.Content?.Length ?? 0).Append('\u001f')
            .Append(Fingerprint(message.Content)).Append('\n');
    }

    private static void AppendViewModelMessage(StringBuilder builder, ChatMessageViewModel message)
    {
        builder.Append(message.MessageId).Append('\u001f')
            .Append(message.Role).Append('\u001f')
            .Append(message.IsHiddenPlaceholder ? '1' : '0').Append('\u001f')
            .Append(message.IsCompaction ? '1' : '0').Append('\u001f')
            .Append(message.IsExpanded ? '1' : '0').Append('\u001f')
            .Append(message.ToolCallId ?? string.Empty).Append('\u001f')
            .Append(message.ToolName).Append('\u001f')
            .Append((int)message.ToolCallStatus).Append('\u001f')
            .Append((int)message.ToolApprovalState).Append('\u001f')
            .Append(Fingerprint(message.Content)).Append('\u001f')
            .Append(Fingerprint(message.ReasoningContent)).Append('\u001f')
            .Append(Fingerprint(message.ToolArgumentsText)).Append('\u001f')
            .Append(Fingerprint(message.ToolApprovalArgumentsPreview)).Append('\u001f')
            .Append(Fingerprint(message.CompactionCardTitle))
            .Append('\n');
    }

    private static string DescribePlan(PlanRun? planRun)
    {
        if (planRun is null)
        {
            return "none";
        }

        return string.Concat(
            planRun.Id, '~',
            planRun.Status, '~',
            planRun.Phase.ToString(), '~',
            planRun.Title ?? string.Empty, '~',
            planRun.Overview ?? string.Empty, '~',
            Fingerprint(planRun.PlanMarkdown), '~',
            planRun.Todos.Count.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Short content fingerprint (FNV-1a 64). Cheap for large tool outputs and changes whenever
    /// the text changes; length is included to make accidental collisions even less likely.
    /// </summary>
    private static string Fingerprint(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "0";
        }

        unchecked
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            var hash = offset;
            foreach (var ch in value)
            {
                hash ^= (byte)ch;
                hash *= prime;
                hash ^= (byte)(ch >> 8);
                hash *= prime;
            }

            return $"{value.Length}:{hash:x16}";
        }
    }

    private static string Hash(StringBuilder builder)
    {
        var bytes = Encoding.UTF8.GetBytes(builder.ToString());
        var digest = SHA256.HashData(bytes);
        return Convert.ToHexString(digest);
    }
}
