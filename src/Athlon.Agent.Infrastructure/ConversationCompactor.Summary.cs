using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;

namespace Athlon.Agent.Infrastructure;

/// <summary>Summary request and handoff reload.</summary>
public sealed partial class ConversationCompactor
{
    private async Task<ChatMessage?> LoadHandoffMessageAsync(string sessionId, CancellationToken cancellationToken)
    {
        var note = await storage.ReadHandoffNoteAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(note) ? null : SessionHandoffNote.CreateMessage(note);
    }

    private static int EstimateCompactedTokens(
        ChatMessage summaryMessage,
        IReadOnlyList<ChatMessage> retainedTail,
        ContextCompactionSettings cfg,
        ChatMessage? handoffMessage = null)
    {
        var messages = new List<ChatMessage>(retainedTail.Count + 2) { summaryMessage };
        if (handoffMessage is not null)
        {
            messages.Add(handoffMessage);
        }

        messages.AddRange(retainedTail);
        return ContextTokenEstimator.Estimate(
            messages,
            cfg.IncludeReasoningInModelContext,
            hygiene: cfg.RequestHistoryHygiene);
    }

    private static AgentModelRequest BuildSummaryRequest(
        IReadOnlyList<ChatMessage> prefix,
        ContextCompactionSettings cfg,
        CompactionExecutionRequest request,
        string? mustPreserve,
        bool computeAuditMetrics,
        bool handoffNotePresent,
        out int? summaryInputCharsBefore,
        out int? summaryInputCharsAfter,
        out int? hygieneSavingsEstimate)
    {
        var pressure = request.Plan?.Pressure;
        var effectiveMaxChars = ResolveSummaryMaxChars(cfg, pressure, request.Force);
        var effectiveMaxTokens = ResolveSummaryMaxTokens(cfg, pressure, request.Force);
        var hygieneSettings = ResolveSummaryHygieneSettings(cfg, pressure, request.Force);
        var runtime = request.RuntimeContext;
        var environmentPrompt = runtime?.EnvironmentPrompt ?? string.Empty;
        var calibrationMultiplier = runtime?.CalibrationMultiplier ?? 1.0;

        summaryInputCharsBefore = null;
        summaryInputCharsAfter = null;
        hygieneSavingsEstimate = null;

        if (computeAuditMetrics)
        {
            var formatted = ConversationSummaryFormatter.FormatMessages(prefix);
            summaryInputCharsBefore = formatted.Length;
            summaryInputCharsAfter = formatted.Length;

            if (formatted.Length > effectiveMaxChars
                || ContextTokenEstimator.EstimateTextTokens(formatted, calibrationMultiplier) > hygieneSettings.MaxToolResultTokens)
            {
                var compacted = RequestHistoryHygiene.CompactTextForSummary(formatted, hygieneSettings);
                formatted = compacted.Text;
                summaryInputCharsBefore = compacted.CharsBefore;
                summaryInputCharsAfter = compacted.CharsAfter;
                hygieneSavingsEstimate = compacted.EstimatedSavingsTokens;
            }

            if (formatted.Length > effectiveMaxChars)
            {
                formatted = ConversationSummaryFormatter.FitToMaxChars(formatted, effectiveMaxChars);
                summaryInputCharsAfter = formatted.Length;
            }
        }

        var built = ModelMessagesForApiBuilder.Build(
            cache: null,
            environmentPrompt,
            SummaryToolTrace.Apply(prefix),
            cfg);
        var hygieneResult = RequestHistoryHygiene.ApplyToModelMessages(built.Messages, hygieneSettings);
        var messages = hygieneResult.Messages.ToList();
        hygieneSavingsEstimate = Math.Max(
            hygieneSavingsEstimate ?? 0,
            built.EstimatedSavingsTokens + hygieneResult.EstimatedSavingsTokens);

        messages.Add(new AgentModelMessage(
            "user",
            BuildSummaryPrompt(
                ConversationCompactionDefaults.DefaultSummaryPrompt,
                ConversationCompactionDefaults.PrecedingMessagesPlaceholder,
                mustPreserve,
                handoffNotePresent)));

        return new AgentModelRequest(
            messages,
            Array.Empty<ToolDefinition>(),
            AllowToolCalls: false,
            MaxTokens: effectiveMaxTokens);
    }

    private static int ResolveSummaryMaxTokens(
        ContextCompactionSettings cfg,
        ContextPressureLevel? pressure,
        bool force) =>
        force || pressure == ContextPressureLevel.Overflow
            ? Math.Max(128, Math.Min(cfg.SummaryMaxTokens, cfg.SummaryMaxTokens / 2))
            : cfg.SummaryMaxTokens;

    private static int ResolveSummaryMaxChars(
        ContextCompactionSettings cfg,
        ContextPressureLevel? pressure,
        bool force) =>
        force || pressure == ContextPressureLevel.Overflow
            ? Math.Max(1024, Math.Min(cfg.MaxConversationCharsForSummary, cfg.MaxConversationCharsForSummary / 4))
            : cfg.MaxConversationCharsForSummary;

    private static RequestHistoryHygieneSettings ResolveSummaryHygieneSettings(
        ContextCompactionSettings cfg,
        ContextPressureLevel? pressure,
        bool force)
    {
        if (!force && pressure != ContextPressureLevel.Overflow)
        {
            return cfg.RequestHistoryHygiene;
        }

        return new RequestHistoryHygieneSettings
        {
            Enabled = cfg.RequestHistoryHygiene.Enabled,
            MaxToolResultLines = Math.Max(16, Math.Min(cfg.RequestHistoryHygiene.MaxToolResultLines, cfg.RequestHistoryHygiene.MaxToolResultLines / 2)),
            MaxToolResultBytes = Math.Max(1024, Math.Min(cfg.RequestHistoryHygiene.MaxToolResultBytes, cfg.RequestHistoryHygiene.MaxToolResultBytes / 2)),
            MaxToolResultTokens = Math.Max(256, Math.Min(cfg.RequestHistoryHygiene.MaxToolResultTokens, cfg.RequestHistoryHygiene.MaxToolResultTokens / 2)),
            MaxToolArgumentStringBytes = Math.Max(256, Math.Min(cfg.RequestHistoryHygiene.MaxToolArgumentStringBytes, cfg.RequestHistoryHygiene.MaxToolArgumentStringBytes / 2)),
            MaxToolArgumentStringTokens = Math.Max(64, Math.Min(cfg.RequestHistoryHygiene.MaxToolArgumentStringTokens, cfg.RequestHistoryHygiene.MaxToolArgumentStringTokens / 2)),
            MaxArrayItems = cfg.RequestHistoryHygiene.MaxArrayItems
        };
    }

    private static string BuildSummaryPrompt(
        string template,
        string formattedMessages,
        string? mustPreserveAppendix,
        bool handoffNotePresent)
    {
        var mustPreserve = string.IsNullOrWhiteSpace(mustPreserveAppendix) ? string.Empty : mustPreserveAppendix.Trim();
        var prompt = template
            .Replace("{must_preserve}", mustPreserve, StringComparison.Ordinal)
            .Replace("{messages}", formattedMessages, StringComparison.Ordinal);
        if (!handoffNotePresent)
        {
            return prompt;
        }

        return prompt + "\n\n" + ConversationCompactionDefaults.HandoffPreservedSummaryOverride;
    }
}
