using System.Text.Json;
using Athlon.Agent.Core.Streaming;

namespace Athlon.Agent.Core.Cli;

public sealed record CliSseFrame(string Event, object Payload);

public static class CliStreamEventMapper
{
    public static CliSseFrame? TryMap(AgentStreamEvent streamEvent) => streamEvent switch
    {
        AgentStreamEvent.TextMessageContent content =>
            new CliSseFrame(CliSseEventNames.Text, new CliTextPayload(content.Delta)),
        AgentStreamEvent.ToolCallStart start =>
            new CliSseFrame(CliSseEventNames.ToolStart, new CliToolStartPayload(start.ToolCallId, start.ToolName)),
        AgentStreamEvent.ToolCallEnd end =>
            new CliSseFrame(CliSseEventNames.ToolEnd, new CliToolEndPayload(end.ToolCallId)),
        AgentStreamEvent.ToolCallOutput output =>
            new CliSseFrame(CliSseEventNames.ToolOutput, new CliToolOutputPayload(output.ToolCallId, output.Delta)),
        _ => null
    };

    public static string Format(string eventName, object payload)
    {
        var json = JsonSerializer.Serialize(payload, payload.GetType(), JsonFileStoreOptions.WebCompactRelaxed);
        return $"event: {eventName}\ndata: {json}\n\n";
    }

    public static string Format(CliSseFrame frame) => Format(frame.Event, frame.Payload);
}
