using System.IO;
using System.Text.Json;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Teams;
using Athlon.Agent.App.ViewModels;

namespace Athlon.Agent.App.Services;

/// <summary>
/// Builds chat-like activity records for Team Trace from member conversation.jsonl / session.json.
/// </summary>
internal static class TeamTraceTranscriptBuilder
{
    // Must match JsonFileStore line/session writers (string enums). Plain Web defaults
    // fail to deserialize MessageRole and silently drop every transcript line.
    private static readonly JsonSerializerOptions JsonOptions = JsonFileStoreOptions.WebCompactRelaxed;
    private const int MaxMessagesPerMember = 240;
    private const int MaxTextChars = 12_000;
    private const int MaxToolDetailChars = 8_000;

    public static IReadOnlyDictionary<string, IReadOnlyList<object>> Build(
        TeamTask task,
        ITeamTaskStore taskStore)
    {
        var map = new Dictionary<string, IReadOnlyList<object>>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(task.LeaderSessionId))
        {
            map["leader"] = LoadMemberActivity(task, taskStore, task.LeaderSessionId);
            map["agg"] = map["leader"];
            map["submit"] = map["leader"];
        }

        foreach (var sub in task.SubTasks)
        {
            if (string.IsNullOrWhiteSpace(sub.MemberSessionId))
            {
                map[sub.Id] = Array.Empty<object>();
                continue;
            }

            map[sub.Id] = LoadMemberActivity(task, taskStore, sub.MemberSessionId);
        }

        return map;
    }

    private static IReadOnlyList<object> LoadMemberActivity(
        TeamTask task,
        ITeamTaskStore taskStore,
        string memberSessionId)
    {
        try
        {
            var dir = taskStore.ResolveMemberDirectory(task.RootSessionId, task.TaskId, memberSessionId);
            var messages = ReadMessages(dir);
            if (messages.Count == 0)
            {
                return Array.Empty<object>();
            }

            if (messages.Count > MaxMessagesPerMember)
            {
                messages = messages.Skip(messages.Count - MaxMessagesPerMember).ToList();
            }

            return Flatten(messages);
        }
        catch
        {
            return Array.Empty<object>();
        }
    }

    private static List<ChatMessage> ReadMessages(string memberDir)
    {
        var list = new List<ChatMessage>();
        var conversationPath = Path.Combine(memberDir, "conversation.jsonl");
        if (File.Exists(conversationPath))
        {
            try
            {
                using var stream = new FileStream(
                    conversationPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    try
                    {
                        var message = JsonSerializer.Deserialize<ChatMessage>(line, JsonOptions);
                        if (message is not null)
                        {
                            list.Add(message);
                        }
                    }
                    catch (JsonException)
                    {
                        // skip malformed display lines
                    }
                }
            }
            catch (IOException)
            {
                // Writer may briefly lock; keep empty and let next poll retry.
            }
        }

        // Prefer whichever source is richer — conversation.jsonl may lag session.json briefly
        // (running tool placeholders often land in session.json first via PersistMessageAsync).
        var fromSession = ReadSessionMessages(memberDir);
        if (fromSession.Count > list.Count)
        {
            return fromSession;
        }

        if (fromSession.Count == list.Count && fromSession.Count > 0)
        {
            // Same count can still mean session.json has newer upserted tool rows.
            var convStamp = TryFileTicks(conversationPath);
            var sessionStamp = TryFileTicks(Path.Combine(memberDir, "session.json"));
            if (sessionStamp > convStamp)
            {
                return fromSession;
            }
        }

        return list.Count > 0 ? list : fromSession;
    }

    private static long TryFileTicks(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.LastWriteTimeUtc.Ticks : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static List<ChatMessage> ReadSessionMessages(string memberDir)
    {
        var list = new List<ChatMessage>();
        var sessionPath = Path.Combine(memberDir, "session.json");
        if (!File.Exists(sessionPath))
        {
            return list;
        }

        try
        {
            using var stream = new FileStream(
                sessionPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            var session = JsonSerializer.Deserialize<AgentSession>(json, JsonOptions);
            if (session?.Messages is { Count: > 0 })
            {
                foreach (var message in session.Messages)
                {
                    if (message.Role is MessageRole.System or MessageRole.Compaction)
                    {
                        continue;
                    }

                    list.Add(message);
                }
            }
        }
        catch (IOException)
        {
            // ignore transient share conflicts
        }
        catch (JsonException)
        {
            // ignore
        }

        return list;
    }

    public static string ComputeFingerprint(
        TeamTask task,
        IReadOnlyList<TeamTraceEvent> events,
        ITeamTaskStore taskStore)
    {
        var sb = new System.Text.StringBuilder(256);
        sb.Append(task.TaskId).Append('|')
            .Append(task.Status).Append('|')
            .Append(task.UpdatedAt.UtcTicks).Append('|')
            .Append(events.Count).Append('|')
            .Append(task.SubTasks.Count);
        foreach (var sub in task.SubTasks)
        {
            sb.Append('|').Append(sub.Id).Append(':').Append(sub.Status)
                .Append(':').Append(sub.Iteration)
                .Append(':').Append(sub.CompletedAt?.UtcTicks ?? 0);
            if (!string.IsNullOrWhiteSpace(sub.MemberSessionId))
            {
                AppendMemberStamp(sb, taskStore.ResolveMemberDirectory(
                    task.RootSessionId, task.TaskId, sub.MemberSessionId));
            }
        }

        if (!string.IsNullOrWhiteSpace(task.LeaderSessionId))
        {
            AppendMemberStamp(sb, taskStore.ResolveMemberDirectory(
                task.RootSessionId, task.TaskId, task.LeaderSessionId));
        }

        return sb.ToString();
    }

    private static void AppendMemberStamp(System.Text.StringBuilder sb, string memberDir)
    {
        AppendFileStamp(sb, Path.Combine(memberDir, "conversation.jsonl"));
        AppendFileStamp(sb, Path.Combine(memberDir, "session.json"));
    }

    private static void AppendFileStamp(System.Text.StringBuilder sb, string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                sb.Append("|0:0");
                return;
            }

            sb.Append('|').Append(info.Length).Append(':').Append(info.LastWriteTimeUtc.Ticks);
        }
        catch
        {
            sb.Append("|?");
        }
    }

    /// <summary>Test seam: merge call+result into one Tools row per toolCallId.</summary>
    internal static IReadOnlyList<object> FlattenForTests(IReadOnlyList<ChatMessage> messages) =>
        Flatten(messages);

    private static IReadOnlyList<object> Flatten(IReadOnlyList<ChatMessage> messages)
    {
        // Index Tool role results by call id so each invocation renders as one row
        // (call + result merged) instead of a permanent "running" twin.
        var resultsByCallId = new Dictionary<string, ParsedToolResult>(StringComparer.OrdinalIgnoreCase);
        var assistantCallIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var message in messages)
        {
            if (message.Role == MessageRole.Assistant)
            {
                var pending = AssistantToolCallsCodec.Deserialize(message.ToolCallsJson);
                if (pending is { Count: > 0 })
                {
                    foreach (var call in pending)
                    {
                        if (!string.IsNullOrWhiteSpace(call.Id))
                        {
                            assistantCallIds.Add(call.Id);
                        }
                    }
                }
            }
            else if (message.Role == MessageRole.Tool)
            {
                var parsed = ParseToolResult(message);
                if (!string.IsNullOrWhiteSpace(parsed.ToolCallId))
                {
                    resultsByCallId[parsed.ToolCallId] = parsed;
                }
            }
        }

        var items = new List<object>(messages.Count);
        foreach (var message in messages)
        {
            switch (message.Role)
            {
                case MessageRole.User:
                    if (!string.IsNullOrWhiteSpace(message.Content))
                    {
                        items.Add(new
                        {
                            kind = "user",
                            at = message.CreatedAt,
                            text = Truncate(message.Content, MaxTextChars)
                        });
                    }

                    break;

                case MessageRole.Assistant:
                    if (!string.IsNullOrWhiteSpace(message.Content)
                        || !string.IsNullOrWhiteSpace(message.ReasoningContent))
                    {
                        items.Add(new
                        {
                            kind = "assistant",
                            at = message.CreatedAt,
                            text = Truncate(message.Content, MaxTextChars),
                            reasoning = Truncate(message.ReasoningContent, MaxTextChars)
                        });
                    }

                    var calls = AssistantToolCallsCodec.Deserialize(message.ToolCallsJson);
                    if (calls is { Count: > 0 })
                    {
                        foreach (var call in calls)
                        {
                            var callArgs = Truncate(
                                ToolMessageDisplayParser.FormatArgumentsFull(call.Arguments, call.Name),
                                MaxToolDetailChars);
                            if (!string.IsNullOrWhiteSpace(call.Id)
                                && resultsByCallId.TryGetValue(call.Id, out var result))
                            {
                                items.Add(new
                                {
                                    kind = "tool",
                                    at = result.At,
                                    toolCallId = call.Id,
                                    toolName = string.IsNullOrWhiteSpace(result.ToolName)
                                        ? call.Name
                                        : result.ToolName,
                                    status = result.Status,
                                    arguments = string.IsNullOrWhiteSpace(result.Arguments)
                                        ? callArgs
                                        : result.Arguments,
                                    summary = result.Summary,
                                    detail = result.Detail
                                });
                            }
                            else
                            {
                                items.Add(new
                                {
                                    kind = "tool",
                                    at = message.CreatedAt,
                                    toolCallId = call.Id,
                                    toolName = call.Name,
                                    status = "running",
                                    arguments = callArgs
                                });
                            }
                        }
                    }

                    break;

                case MessageRole.Tool:
                    var orphan = ParseToolResult(message);
                    // Already merged into the assistant tool_call row.
                    if (!string.IsNullOrWhiteSpace(orphan.ToolCallId)
                        && assistantCallIds.Contains(orphan.ToolCallId))
                    {
                        break;
                    }

                    items.Add(new
                    {
                        kind = "tool",
                        at = orphan.At,
                        toolCallId = orphan.ToolCallId,
                        toolName = string.IsNullOrWhiteSpace(orphan.ToolName) ? "tool" : orphan.ToolName,
                        status = orphan.Status,
                        arguments = orphan.Arguments,
                        summary = orphan.Summary,
                        detail = orphan.Detail
                    });
                    break;

                case MessageRole.System:
                case MessageRole.Summary:
                case MessageRole.Compaction:
                    // Keep Trace focused on turn activity; skip prompt/system noise.
                    break;
            }
        }

        return items;
    }

    private static ParsedToolResult ParseToolResult(ChatMessage message)
    {
        ToolMessageDisplayParser.ParseToolContent(
            message.Content,
            out var toolCallId,
            out var toolName,
            out _,
            out var summary,
            out var detail,
            out var argumentsText,
            out var status);
        return new ParsedToolResult(
            message.CreatedAt,
            toolCallId,
            string.IsNullOrWhiteSpace(toolName) ? "tool" : toolName,
            status switch
            {
                ToolCallDisplayStatus.Failed => "failed",
                ToolCallDisplayStatus.Running => "running",
                _ => "succeeded"
            },
            Truncate(argumentsText, MaxToolDetailChars),
            Truncate(summary, MaxTextChars),
            Truncate(string.IsNullOrWhiteSpace(detail) ? message.Content : detail, MaxToolDetailChars));
    }

    private sealed record ParsedToolResult(
        DateTimeOffset At,
        string? ToolCallId,
        string ToolName,
        string Status,
        string? Arguments,
        string? Summary,
        string? Detail);

    private static string? Truncate(string? value, int maxChars)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return value.Length <= maxChars
            ? value
            : value[..maxChars] + "…";
    }
}
