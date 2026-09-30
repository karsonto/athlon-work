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

/// <summary>Timeline cards: activity, files, assistant HTML, tools, approvals and plans.</summary>
internal static partial class ChatEventSerializer
{
    public static string SerializeTurnActivity(
        TurnActivitySummary summary,
        bool upsert = false,
        long? seq = null,
        string? turnAnchorId = null,
        int activityBlockIndex = 0,
        string? entryId = null)
    {
        var items = summary.Items.Select(item => new
        {
            kind = item.Kind.ToString().ToLowerInvariant(),
            verb = item.Kind == TurnActivityKind.Tool
                ? item.Verb
                : LocalizeActivityVerb(item.Kind),
            detail = item.Detail,
            path = item.Path,
            added = item.Added,
            removed = item.Removed,
            body = item.Body,
            status = item.Status,
            statusLabel = item.Status is null ? null : LocalizeActivityStatus(item.Status),
            messageId = item.MessageId,
            toolCallId = item.ToolCallId,
            lines = item.DiffLines?.Select(line => new
            {
                kind = line.Kind,
                text = line.Text,
                count = line.Count
            })
        }).ToList();

        return SerializeAgui("TURN_ACTIVITY", new
        {
            seq,
            entryId = ResolveActivityEntryId(entryId, turnAnchorId, activityBlockIndex),
            upsert,
            editedFileCount = summary.EditedFileCount,
            exploredFileCount = summary.ExploredFileCount,
            searchCount = summary.SearchCount,
            commandCount = summary.CommandCount,
            thoughtCount = summary.ThoughtCount,
            totalAdded = summary.TotalAdded,
            totalRemoved = summary.TotalRemoved,
            durationMs = summary.DurationMs,
            items
        });
    }

    /// <summary>
    /// Deterministic activity-fold entry id. A turn can hold several folds (one per assistant
    /// bubble boundary), so the id is anchored on the turn boundary plus the fold's ordinal inside
    /// that turn. Both the live publish and the replay derive the same id, so an upsert rewrites the
    /// same entry instead of stacking a twin.
    /// </summary>
    public static string? ResolveActivityEntryId(
        string? entryId,
        string? turnAnchorId,
        int activityBlockIndex)
    {
        if (!string.IsNullOrWhiteSpace(entryId))
        {
            return entryId;
        }

        if (string.IsNullOrWhiteSpace(turnAnchorId))
        {
            return null;
        }

        return "activity:" + turnAnchorId + ":" + Math.Max(0, activityBlockIndex);
    }

    public static string SerializeTurnActivityOutput(string toolCallId, string delta) =>
        SerializeAgui("TURN_ACTIVITY_OUTPUT", new { toolCallId, delta });

    private static string LocalizeActivityVerb(TurnActivityKind kind) => kind switch
    {
        TurnActivityKind.Edited => Strings.Get("Chat_ActivityVerbEdited"),
        TurnActivityKind.Read => Strings.Get("Chat_ActivityVerbRead"),
        TurnActivityKind.Searched => Strings.Get("Chat_ActivityVerbSearched"),
        TurnActivityKind.Explored => Strings.Get("Chat_ActivityVerbExplored"),
        TurnActivityKind.Command => Strings.Get("Chat_ActivityVerbCommand"),
        TurnActivityKind.Thought => Strings.Get("Chat_ActivityVerbThought"),
        _ => kind.ToString()
    };

    private static string LocalizeActivityStatus(string status) => status switch
    {
        "preparing" => Strings.Get("Chat_ToolStatusPreparing"),
        "running" => Strings.Get("Chat_ToolStatusRunning"),
        "awaiting_approval" => Strings.Get("Chat_ToolApprovalPending"),
        "approval_denied" => Strings.Get("Chat_ToolApprovalDeniedStatus"),
        "failed" => Strings.Get("Chat_ToolStatusFailed"),
        "cancelled" => Strings.Get("Chat_ToolStatusCancelled"),
        "succeeded" => Strings.Get("Chat_ToolStatusSucceeded"),
        _ => status
    };

    public static string SerializeFilesChanged(
        IReadOnlyList<ModifiedFileViewModel> files,
        bool upsert = false,
        long? seq = null,
        string? turnAnchorId = null,
        string? entryId = null)
    {
        var resolvedEntryId = entryId
            ?? (turnAnchorId is null ? null : "files:" + turnAnchorId);
        if (files.Count == 0)
        {
            return SerializeAgui("FILES_CHANGED", new
            {
                seq,
                entryId = resolvedEntryId,
                upsert,
                files = Array.Empty<object>()
            });
        }

        var payload = files.Select(file => new
        {
            path = file.RelativePath,
            displayName = file.DisplayName,
            added = file.AddedCount,
            removed = file.RemovedCount,
            lines = UnifiedDiffDisplayParser.Parse(file.UnifiedDiffText, foldContext: true)
                .Select(line => new
                {
                    kind = line.Kind switch
                    {
                        DiffLineKind.Added => "added",
                        DiffLineKind.Removed => "removed",
                        DiffLineKind.Context => "context",
                        DiffLineKind.HunkHeader => "hunkHeader",
                        DiffLineKind.Header => "header",
                        DiffLineKind.Collapsed => "collapsed",
                        _ => "context"
                    },
                    text = line.Text,
                    count = line.CollapsedCount
                })
        }).ToList();

        return SerializeAgui("FILES_CHANGED", new
        {
            seq,
            entryId = resolvedEntryId,
            upsert,
            files = payload
        });
    }

    public static string SerializeStaticAssistantHtml(
        ChatMessageViewModel message,
        bool streaming = false,
        int? responseDurationMs = null,
        long? seq = null) =>
        SerializeAgui("STATIC_ASSISTANT_HTML", new
        {
            seq,
            messageId = message.MessageId,
            markdown = message.Content,
            html = MarkdownHtmlRenderer.ToHtmlFragment(message.Content),
            createIfMissing = true,
            streaming,
            responseDurationMs = streaming ? null : responseDurationMs
        });

    public static string SerializeCompactionCheckpoint(ChatMessageViewModel message)
    {
        var id = string.IsNullOrWhiteSpace(message.ToolCallId) ? message.MessageId : message.ToolCallId;

        return SerializeAgui("COMPACTION_CHECKPOINT", new
        {
            id,
            title = string.IsNullOrWhiteSpace(message.CompactionCardTitle)
                ? Strings.Get("Chat_CompactionDefault")
                : message.CompactionCardTitle,
            summary = message.ToolSummary,
            header = message.ToolHeader,
            detail = message.IsToolRunning ? string.Empty : message.ToolDetail,
            detailsLabel = Strings.Get("Chat_CompactionDetails"),
            status = SerializeToolStatus(message.ToolCallStatus, message.ToolApprovalState),
            running = message.IsToolRunning
        });
    }

    public static string SerializeToolResultMarkdown(ChatMessageViewModel message)
    {
        if (message.IsCompaction)
        {
            return SerializeCompactionCheckpoint(message);
        }

        var toolCallId = string.IsNullOrWhiteSpace(message.ToolCallId) ? message.MessageId : message.ToolCallId;
        var detail = ResolveToolResultDetail(message);
        if (string.IsNullOrWhiteSpace(detail))
        {
            return "{}";
        }

        return SerializeAgui("TOOL_CALL_RESULT", new
        {
            toolCallId,
            content = detail,
            messageId = message.MessageId,
            header = message.ToolHeader,
            summary = message.ToolSummary,
            status = SerializeToolStatus(message.ToolCallStatus, message.ToolApprovalState),
            markdown = detail,
            html = RenderToolResultHtml(message, detail)
        });
    }

    public static string SerializeToolApprovalRequest(
        PendingToolApproval approval,
        string arguments) =>
        SerializeAgui("TOOL_APPROVAL_REQUEST", new
        {
            toolCallId = approval.ToolCallId,
            toolName = approval.ToolName,
            arguments
        });

    public static string SerializeToolApprovalResolved(
        string toolCallId,
        ToolApprovalDecision decision) =>
        SerializeAgui("TOOL_APPROVAL_RESOLVED", new
        {
            toolCallId,
            approved = decision == ToolApprovalDecision.Approved
        });

    public static string SerializePlanReady(PlanRun run, long? seq = null) =>
        SerializeAgui("PLAN_READY", new
        {
            seq,
            runId = run.Id,
            title = run.Title,
            overview = run.Overview,
            markdown = run.PlanMarkdown,
            html = MarkdownHtmlRenderer.ToHtmlFragment(run.PlanMarkdown),
            todos = run.Todos.Select(t => new { id = t.Id, content = t.Content }).ToList(),
            built = run.Phase == PlanPhase.Done
                || string.Equals(run.Status, PlanRunStatuses.Approved, StringComparison.OrdinalIgnoreCase)
        });

    /// <summary>
    /// Removes a plan-ready card from the timeline (plan abandoned). Replay never emits it; it
    /// only clears a card the live dispatcher had published from the plan store.
    /// </summary>
    public static string SerializePlanCleared(string runId) =>
        SerializeAgui("PLAN_CLEARED", new { runId });

    private static string RenderToolResultHtml(ChatMessageViewModel message, string detail) =>
        message.IsCompaction && message.IsToolRunning
            ? MarkdownHtmlRenderer.ToPlainTextHtmlFragment(detail)
            : MarkdownHtmlRenderer.ToHtmlFragment(detail);
}
