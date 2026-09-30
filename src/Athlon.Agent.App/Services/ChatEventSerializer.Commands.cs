using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Athlon.Agent.App.Resources;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Plan;
using Athlon.Agent.Core.Streaming;

namespace Athlon.Agent.App.Services;

/// <summary>WebView page commands (replay, append, switch, prepend).</summary>
internal static partial class ChatEventSerializer
{
    public static string SerializeEventsToJsonArray(IReadOnlyList<string> eventJsonStrings)
    {
        if (eventJsonStrings.Count == 0)
        {
            return "[]";
        }

        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var eventJson in eventJsonStrings)
            {
                using var doc = JsonDocument.Parse(eventJson);
                doc.RootElement.WriteTo(writer);
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static string SerializeReplayCommand(
        IReadOnlyList<ChatMessageViewModel> messages,
        bool showToolCalls = false,
        IReadOnlyList<ChatMessage>? activitySourceMessages = null) =>
        SerializeWebMessageCommand(
            "replay",
            BuildReplayEvents(messages, showToolCalls, activitySourceMessages: activitySourceMessages));

    public static string SerializeAppendCommand(
        IReadOnlyList<ChatMessageViewModel> messages,
        bool showToolCalls = false,
        IReadOnlyList<ChatMessage>? activitySourceMessages = null) =>
        SerializeWebMessageCommand(
            "append",
            BuildReplayEvents(
                messages,
                showToolCalls,
                includeReset: false,
                activitySourceMessages: activitySourceMessages));

    public static string SerializeEventsCommand(
        string command,
        IReadOnlyList<string> events,
        int? renderGeneration = null,
        bool replayComplete = false,
        string? sessionId = null,
        string? revision = null) =>
        SerializeWebMessageCommand(
            command,
            events,
            renderGeneration: renderGeneration,
            replayComplete: replayComplete,
            sessionId: sessionId,
            revision: revision);

    /// <summary>
    /// Asks the timeline to swap a previously rendered session's DOM back in (the Phase-4 fast
    /// path). The page answers <c>snapshotRestored</c> or <c>snapshotMiss</c>; a miss (or no answer
    /// in time) means the caller must run the normal replay.
    /// </summary>
    public static string SerializeSwitchSessionCommand(string sessionId, string revision, int renderGeneration) =>
        JsonSerializer.Serialize(
            new
            {
                command = "switchSession",
                sessionId,
                revision,
                renderGeneration
            },
            AppJson.Options);

    /// <summary>Drops a session's rendered DOM snapshot in the page (content changed / evicted).</summary>
    public static string SerializeInvalidateSessionCommand(string sessionId) =>
        JsonSerializer.Serialize(
            new
            {
                command = "invalidateSession",
                sessionId
            },
            AppJson.Options);

    public static string SerializeResetCommand() =>
        SerializeWebMessageCommand("reset", Array.Empty<string>());

    public static string SerializePrependCommand(
        IReadOnlyList<ChatMessageViewModel> messages,
        bool showToolCalls,
        bool hasOlderMessages,
        IReadOnlyList<ChatMessage>? activitySourceMessages = null) =>
        SerializeWebMessageCommand(
            "prepend",
            BuildReplayEvents(
                messages,
                showToolCalls,
                includeReset: false,
                activitySourceMessages: activitySourceMessages),
            hasOlderMessages);

    public static string SerializeHistoryAvailabilityCommand(bool hasOlderMessages) =>
        JsonSerializer.Serialize(new
        {
            command = "historyAvailability",
            hasOlderMessages
        }, AppJson.Options);
}
