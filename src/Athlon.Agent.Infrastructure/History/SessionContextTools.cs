using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;

namespace Athlon.Agent.Infrastructure.History;

public sealed class HistoryListTranscriptsTool(
    IFileStorageService storage,
    IActiveAgentSessionContext sessions) : IAgentTool, IParallelizableAgentTool
{
    public ToolDefinition Definition => new(
        Name: "history_list_transcripts",
        Description:
            "List archived conversation transcripts for this session. Each entry is a file name, write time, and message count. The compaction summary is an outline; use these files to recover original messages.",
        ToolSchema.Object().Build());

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!TrySession(sessions, out var sessionId, out var error))
        {
            return error;
        }

        var transcripts = await storage.ListSessionTranscriptsAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (transcripts.Count == 0)
        {
            return ToolResult.Success("No transcripts", "No archived transcripts for this session.");
        }

        var builder = new StringBuilder();
        foreach (var transcript in transcripts)
        {
            builder.Append(transcript.FileName)
                .Append(" written=")
                .Append(transcript.WrittenAt.ToString("O"))
                .Append(" messages=")
                .Append(transcript.MessageCount)
                .AppendLine();
        }

        return ToolResult.Success("Listed transcripts", builder.ToString().TrimEnd());
    }

    internal static bool TrySession(IActiveAgentSessionContext sessions, out string sessionId, out ToolResult error)
    {
        sessionId = sessions.SessionId ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            error = null!;
            return true;
        }

        error = ToolResult.Failure("No session", "This tool requires an active agent session.");
        return false;
    }
}

public sealed class HistoryReadTranscriptTool(
    IFileStorageService storage,
    IActiveAgentSessionContext sessions) : IAgentTool, IParallelizableAgentTool
{
    public ToolDefinition Definition => new(
        Name: "history_read_transcript",
        Description:
            "Read a character range from one archived transcript in this session. Pass the file name from history_list_transcripts unchanged. offset_chars is zero-based.",
        ToolSchema.Object()
            .String("file_name", "Transcript file name, for example transcript_1710000000.jsonl.", required: true, minLength: 1)
            .Integer("offset_chars", "Zero-based character offset.", minimum: 0)
            .Integer("limit_chars", "Maximum characters to return.", minimum: 1)
            .Build());

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!HistoryListTranscriptsTool.TrySession(sessions, out var sessionId, out var error))
        {
            return error;
        }

        if (!invocation.Arguments.TryGetString("file_name", out var fileName) || string.IsNullOrWhiteSpace(fileName))
        {
            return ToolResult.Failure("Missing file_name", "file_name is required.");
        }

        var offset = invocation.Arguments.GetInt32("offset_chars");
        var limit = invocation.Arguments.TryGetInt32("limit_chars", out var requested)
            ? requested
            : ContextTokenEstimator.EstimateCharacterBudget(SessionArchiveLimits.MaxToolOutputTokens);
        var text = await storage.ReadSessionTranscriptAsync(sessionId, fileName, offset, limit, cancellationToken)
            .ConfigureAwait(false);
        if (text is null)
        {
            return ToolResult.Failure("Transcript not found", "No transcript with that file name in this session.");
        }

        return ToolResult.Success("Read transcript", SessionArchiveLimits.Truncate(text));
    }
}

public sealed class HistorySearchTranscriptsTool(
    IFileStorageService storage,
    IActiveAgentSessionContext sessions) : IAgentTool, IParallelizableAgentTool
{
    public ToolDefinition Definition => new(
        Name: "history_search_transcripts",
        Description:
            "Search archived transcripts in this session for a case-sensitive literal substring. Returns file name, character offset, and a short preview.",
        ToolSchema.Object()
            .String("query", "Case-sensitive literal substring.", required: true, minLength: 1)
            .Integer("limit", "Maximum matches.", minimum: 1, maximum: 20)
            .Integer("preview_chars", "Characters of preview around each match.", minimum: 40, maximum: 500)
            .Build());

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!HistoryListTranscriptsTool.TrySession(sessions, out var sessionId, out var error))
        {
            return error;
        }

        if (!invocation.Arguments.TryGetString("query", out var query) || string.IsNullOrEmpty(query))
        {
            return ToolResult.Failure("Missing query", "query is required.");
        }

        var limit = invocation.Arguments.GetInt32("limit", 8);
        var preview = invocation.Arguments.GetInt32("preview_chars", 240);
        var matches = await storage.SearchSessionTranscriptsAsync(sessionId, query, limit, preview, cancellationToken)
            .ConfigureAwait(false);
        if (matches.Count == 0)
        {
            return ToolResult.Success("No matches", "No archived transcript contains that text.");
        }

        var builder = new StringBuilder();
        foreach (var match in matches)
        {
            builder.Append(match.FileName)
                .Append(" offset=")
                .Append(match.Offset)
                .AppendLine();
            builder.AppendLine(match.Preview);
            builder.AppendLine("---");
        }

        return ToolResult.Success("Searched transcripts", SessionArchiveLimits.Truncate(builder.ToString().TrimEnd()));
    }
}

public sealed class HistoryReadEvictedTool(
    IFileStorageService storage,
    IActiveAgentSessionContext sessions) : IAgentTool, IParallelizableAgentTool
{
    public ToolDefinition Definition => new(
        Name: "history_read_evicted",
        Description:
            "Read the full archived body of a tool result that was evicted from the active context. Pass the tool call id unchanged.",
        ToolSchema.Object()
            .String("tool_call_id", "Tool call id of the evicted result.", required: true, minLength: 1)
            .Build());

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!HistoryListTranscriptsTool.TrySession(sessions, out var sessionId, out var error))
        {
            return error;
        }

        if (!invocation.Arguments.TryGetString("tool_call_id", out var toolCallId) || !IsSafeToolCallId(toolCallId))
        {
            return ToolResult.Failure("Invalid tool_call_id", "tool_call_id must be a single path segment.");
        }

        var body = await storage.TryReadEvictedToolResultAsync(sessionId, toolCallId, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrEmpty(body))
        {
            return ToolResult.Failure("Not found", "No evicted tool result with that id in this session.");
        }

        return ToolResult.Success("Read evicted tool result", SessionArchiveLimits.Truncate(body));
    }

    internal static bool IsSafeToolCallId(string? toolCallId) =>
        !string.IsNullOrWhiteSpace(toolCallId)
        && toolCallId.Length <= 128
        && toolCallId.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) < 0
        && !toolCallId.Contains("..", StringComparison.Ordinal);
}

public sealed class SessionNoteAppendTool(
    IFileStorageService storage,
    IActiveAgentSessionContext sessions) : IAgentTool
{
    public ToolDefinition Definition => new(
        Name: "session_note_append",
        Description:
            "Append a short handoff note for this session. Write the goal, confirmed decisions, failed paths, next step, and verification commands before requesting compaction. The note is kept locally and reattached after compaction. It is not long-term memory.",
        ToolSchema.Object()
            .String("text", "Text appended exactly as provided.", required: true, minLength: 1)
            .Build());

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!HistoryListTranscriptsTool.TrySession(sessions, out var sessionId, out var error))
        {
            return error;
        }

        if (!invocation.Arguments.TryGetString("text", out var text) || string.IsNullOrWhiteSpace(text))
        {
            return ToolResult.Failure("Missing text", "text is required.");
        }

        var appended = await storage.TryAppendHandoffNoteAsync(sessionId, text, cancellationToken).ConfigureAwait(false);
        if (!appended)
        {
            return ToolResult.Failure(
                "Note not written",
                $"The handoff note must stay within {SessionHandoffNote.MaxChars} characters.");
        }

        return ToolResult.Success("Appended handoff note", "Handoff note updated.");
    }
}

public sealed class SessionNoteReadTool(
    IFileStorageService storage,
    IActiveAgentSessionContext sessions) : IAgentTool, IParallelizableAgentTool
{
    public ToolDefinition Definition => new(
        Name: "session_note_read",
        Description: "Read the current session handoff note.",
        ToolSchema.Object().Build());

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!HistoryListTranscriptsTool.TrySession(sessions, out var sessionId, out var error))
        {
            return error;
        }

        var note = await storage.ReadHandoffNoteAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(note))
        {
            return ToolResult.Success("No handoff note", "This session has no handoff note yet.");
        }

        return ToolResult.Success("Read handoff note", SessionArchiveLimits.Truncate(note));
    }
}

public sealed class RequestContextCompactTool(
    IContextCompactRequestStore compactRequests,
    IActiveAgentSessionContext sessions) : IAgentTool
{
    public ToolDefinition Definition => new(
        Name: "request_context_compact",
        Description:
            "Ask the runtime to compact this session on the next model round. Does not clear the workspace, environment, or session. Call session_note_append first. Use only when the current phase can close and the token budget is getting tight.",
        ToolSchema.Object().Build());

    public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!HistoryListTranscriptsTool.TrySession(sessions, out var sessionId, out var error))
        {
            return Task.FromResult(error);
        }

        compactRequests.Request(sessionId);
        return Task.FromResult(ToolResult.Success(
            "Compaction requested",
            "The next model round will compact this session without clearing the environment."));
    }
}
