using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Memory;
using Athlon.Agent.Core.TrainingData;
using Athlon.Agent.Core.Compaction;
using System.Diagnostics;

namespace Athlon.Agent.Infrastructure.Memory;

/// <summary>
/// Extracts new long-term memories from a finished conversation turn via LLM,
/// then appends them to today's daily memory ledger.
///
/// When the account-level habit profile is due (see <see cref="ProfileRefreshPolicy"/>), the same
/// single LLM call is reused to also rewrite <c>USER.md</c> and/or <c>LESSONS.md</c>. When nothing
/// is due, the prompt and output contract stay byte-for-byte identical to the memory-only path.
/// </summary>
public sealed class MemoryFlushService(
    ILongTermMemory longTermMemory,
    IAgentModelClient modelClient,
    ISessionUsageAccumulator sessionUsageAccumulator,
    IFileStorageService storage,
    IActiveAgentSessionContext activeSessionContext,
    AppSettings settings,
    IProfileMemory profileMemory,
    ProfileRefreshPolicy refreshPolicy,
    IAppLogger logger)
{
    internal const string FlushGuidelines = """
Output ONLY the extracted memories as a markdown bullet list. Each item should be a concise, self-contained fact. Include dates, names, and specifics when available.

If there is nothing worth remembering, respond with exactly: NO_REPLY

Guidelines:
- Extract user preferences, personal information, and stable project decisions
- Capture important technical decisions and their rationale
- Note commitments and deadlines
- Ignore routine greetings, tool invocations, ephemeral status updates, and the current task skeleton (goal, failed paths, and the immediate next step). Those belong in the session handoff note

IMPORTANT:
- You are writing to TODAY's daily memory ledger (memory/YYYY-MM-DD.md), NOT to MEMORY.md.
- MEMORY.md is the curated long-term memory and is shown ONLY as read-only context below. Do NOT restate facts already covered by MEMORY.md or by today's earlier entries.
- Keep each bullet point independent and self-contained.
""";

    internal static readonly string FlushSystemPrompt =
        "You are a memory extraction assistant. Analyze the conversation below and extract durable facts, preferences, and stable decisions that should be remembered later in this session. Do not extract the live task skeleton.\n\n" + FlushGuidelines;

    internal static readonly string FlushInstruction =
        "Analyze the preceding conversation and extract durable facts, preferences, and stable decisions that should be remembered later in this session. Do not extract the live task skeleton.\n\n" + FlushGuidelines + "\n- The conversation to extract from is in the preceding messages.";

    private readonly IAppLogger _logger = logger.ForContext("MemoryFlushService");
    private readonly MemorySettings _cfg = settings.Memory;
    private readonly ProfileSettings _profileCfg = settings.Profile;

    public async Task<MemoryFlushResult> FlushAsync(
        MemoryTurnContext context,
        CancellationToken cancellationToken = default)
    {
        if (!HasExtractableConversation(context.Messages))
        {
            return MemoryFlushResult.Skipped;
        }

        var now = DateTimeOffset.UtcNow;
        var existingMemory = await longTermMemory.ReadCuratedAsync(cancellationToken);
        var today = DateTime.UtcNow;
        var existingDaily = await longTermMemory.ReadDailyAsync(today, cancellationToken);

        var profile = await ResolveProfileWorkAsync(context, now, cancellationToken);
        var request = BuildRequest(context, existingMemory, existingDaily, profile);

        if (profile.Due.Count > 0)
        {
            // Start the cooldown before the call so a failure does not retry on every turn.
            refreshPolicy.MarkAttempted(profile.Due, now);
        }

        AgentModelResponse response;
        var sessionId = activeSessionContext.SessionId ?? "memory-flush";
        var attemptId = IdGen.NewId();
        var stopwatch = Stopwatch.StartNew();
        try
        {
            response = await modelClient.CompleteAsync(request, cancellationToken: cancellationToken);
            var usage = ModelUsageAccounting.Resolve(request, response);
            stopwatch.Stop();
            sessionUsageAccumulator.RecordCall(sessionId, attemptId, ModelCallPurpose.Memory, usage);
            await storage.AppendAttemptEventAsync(
                sessionId,
                new AgentAttemptEvent(
                    DateTimeOffset.UtcNow, attemptId, sessionId, sessionId, AgentAttemptKind.Model,
                    ModelCallPurpose.Memory, null, ToolCatalogFingerprint.Compute(request.Tools),
                    settings.Model.ModelName, usage.PromptTokens ?? 0, usage.CompletionTokens ?? 0,
                    "success", null, stopwatch.ElapsedMilliseconds),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            var promptTokens = ContextTokenEstimator.EstimateModelRequest(request);
            sessionUsageAccumulator.RecordCall(
                sessionId, attemptId, ModelCallPurpose.Memory, new ModelUsage(promptTokens, 0, promptTokens));
            await storage.AppendAttemptEventAsync(
                sessionId,
                new AgentAttemptEvent(
                    DateTimeOffset.UtcNow, attemptId, sessionId, sessionId, AgentAttemptKind.Model,
                    ModelCallPurpose.Memory, null, ToolCatalogFingerprint.Compute(request.Tools),
                    settings.Model.ModelName, promptTokens, 0, "failure", ex.GetType().Name,
                    stopwatch.ElapsedMilliseconds),
                CancellationToken.None).ConfigureAwait(false);
            _logger.Warning("Memory flush LLM call failed: {Error}", ex.Message);
            return MemoryFlushResult.Failed(ex.Message);
        }

        return await ApplyResponseAsync(response, profile, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<MemoryFlushResult> ApplyResponseAsync(
        AgentModelResponse response,
        ProfileWork profile,
        CancellationToken cancellationToken)
    {
        var raw = response.Content?.Trim();

        string? memorySection;
        IReadOnlyDictionary<ProfileKind, string> changed;
        if (profile.Due.Count == 0)
        {
            // Legacy single-section path: no parsing, no behavior change.
            memorySection = raw;
            changed = new Dictionary<ProfileKind, string>();
        }
        else
        {
            var parsed = ProfileFlushOutputParser.Parse(raw, profile.Due, profile.Current);
            memorySection = parsed.MemorySection;
            changed = parsed.ChangedDocuments;
        }

        var flushedMemory = !string.IsNullOrWhiteSpace(memorySection)
            && !string.Equals(memorySection, "NO_REPLY", StringComparison.Ordinal);

        if (flushedMemory)
        {
            var dailyEntry = $"\n## Memory Flush — {AppTimeZone.Now:O}\n{memorySection}\n";
            await longTermMemory.AppendDailyAsync(dailyEntry, cancellationToken).ConfigureAwait(false);
            _logger.Information("Flushed {Length} chars to daily memory ledger", memorySection!.Length);
        }

        foreach (var (kind, content) in changed)
        {
            await profileMemory.WriteAsync(kind, content, cancellationToken).ConfigureAwait(false);
            _logger.Information(
                "Updated profile document {File} ({Chars} chars)",
                ProfileKinds.FileName(kind),
                content.Length);
        }

        if (!flushedMemory && changed.Count == 0)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                _logger.Debug("No memories to flush");
            }

            return MemoryFlushResult.Skipped;
        }

        return MemoryFlushResult.Success(memorySection ?? string.Empty);
    }

    /// <summary>Reads the two profile documents and decides which (if any) this flush should rewrite.</summary>
    private async Task<ProfileWork> ResolveProfileWorkAsync(
        MemoryTurnContext context,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!_profileCfg.Enabled)
        {
            return ProfileWork.None;
        }

        var userDocument = await profileMemory.ReadAsync(ProfileKind.User, cancellationToken).ConfigureAwait(false);
        var lessonsDocument = await profileMemory.ReadAsync(ProfileKind.Lessons, cancellationToken).ConfigureAwait(false);

        var corrections = new List<CorrectionDetector.CorrectionTrajectory>();
        if (lessonsDocument is null || lessonsDocument.UpdatedAt is null)
        {
            corrections.AddRange(CorrectionDetector.Detect(context.Messages));
        }
        else
        {
            var anchor = lessonsDocument.UpdatedAt.Value;
            corrections.AddRange(
                CorrectionDetector.Detect(context.Messages)
                    .Where(t => t.CorrectionMessage.CreatedAt > anchor));
        }

        DateTimeOffset? newestCorrectionAt = corrections.Count == 0
            ? null
            : corrections.Max(t => t.CorrectionMessage.CreatedAt);

        var due = refreshPolicy.GetDueKinds(userDocument, lessonsDocument, newestCorrectionAt, now);

        var current = new Dictionary<ProfileKind, string>(2);
        if (userDocument is not null)
        {
            current[ProfileKind.User] = userDocument.Content;
        }

        if (lessonsDocument is not null)
        {
            current[ProfileKind.Lessons] = lessonsDocument.Content;
        }

        return new ProfileWork(due, current, corrections);
    }

    private AgentModelRequest BuildRequest(
        MemoryTurnContext context,
        string? existingMemory,
        string? existingDaily,
        ProfileWork profile)
    {
        var ledger = BuildLedgerAppendix(existingMemory, existingDaily, profile);
        var instruction = BuildInstruction(profile);
        var sourceMessages = WithoutHandoffNotes(context.Messages);
        var maxTokens = profile.Due.Count == 0
            ? _cfg.SummaryMaxTokens
            : Math.Max(_cfg.SummaryMaxTokens, 2048);

        if (!string.IsNullOrWhiteSpace(context.EnvironmentPrompt))
        {
            var built = ModelMessagesForApiBuilder.Build(
                cache: null,
                context.EnvironmentPrompt,
                sourceMessages,
                settings.ContextCompaction);
            var messages = built.Messages.ToList();
            messages.Add(new AgentModelMessage("user", instruction + ledger));
            return new AgentModelRequest(
                messages,
                context.Tools ?? Array.Empty<ToolDefinition>(),
                AllowToolCalls: false,
                MaxTokens: maxTokens);
        }

        var systemPrompt = profile.Due.Count == 0
            ? FlushSystemPrompt
            : FlushSystemPrompt + "\n\n" + BuildProfileContract(profile);

        var conversationText = SerializeMessages(sourceMessages);
        return new AgentModelRequest(
            [
                new AgentModelMessage("system", systemPrompt),
                new AgentModelMessage("user", "Extract NEW memories from this conversation window (skip anything already covered above):" + ledger + "\n\n" + conversationText)
            ],
            Array.Empty<ToolDefinition>(),
            AllowToolCalls: false,
            MaxTokens: maxTokens);
    }

    private string BuildInstruction(ProfileWork profile)
    {
        if (profile.Due.Count == 0)
        {
            return FlushInstruction;
        }

        return FlushInstruction + "\n\n" + BuildProfileContract(profile);
    }

    /// <summary>Output contract for the profile sections requested this turn.</summary>
    private string BuildProfileContract(ProfileWork profile)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Reply with exactly these sections, in this order, and nothing else:");
        sb.AppendLine();
        sb.AppendLine("## MEMORY");
        sb.AppendLine("- durable facts for the current project session, or NO_REPLY");

        foreach (var kind in profile.Due)
        {
            var title = ProfileKinds.SectionTitle(kind);
            sb.AppendLine();
            sb.AppendLine($"## {title}");
            sb.AppendLine($"The COMPLETE updated {ProfileKinds.FileName(kind)} content, or UNCHANGED.");
            sb.AppendLine();
            sb.AppendLine($"{title} rules:");
            if (kind == ProfileKind.User)
            {
                sb.AppendLine("- Record only stable traits about the person: how they want to be addressed,");
                sb.AppendLine("  language, tone, response format, workflow conventions they repeat.");
                sb.AppendLine("- Never record one-off task content, file paths, credentials, or project facts.");
                sb.AppendLine($"- Merge duplicates instead of appending. Keep it under {_profileCfg.UserMaxChars} characters.");
            }
            else
            {
                sb.AppendLine("- Turn each new correction below into a reusable rule shaped as \"situation -> correct approach\".");
                sb.AppendLine("- Merge with existing entries; replace an entry that a new correction contradicts.");
                sb.AppendLine("- Never record the failed command output verbatim or anything project-specific.");
                sb.AppendLine($"- Keep it under {_profileCfg.LessonsMaxChars} characters.");
            }
        }

        if (profile.Corrections.Count > 0 && profile.Due.Contains(ProfileKind.Lessons))
        {
            sb.AppendLine();
            sb.AppendLine("New corrections:");
            foreach (var correction in profile.Corrections)
            {
                sb.AppendLine("- " + DescribeCorrection(correction));
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string DescribeCorrection(CorrectionDetector.CorrectionTrajectory correction)
    {
        var tool = correction.FailedToolCall.Name;
        var userAsk = Truncate(correction.CorrectionMessage.Content, 300);
        return $"tool `{tool}` failed; user correction: {userAsk}";
    }

    private static string Truncate(string? value, int maxChars)
    {
        var text = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= maxChars ? text : text[..maxChars] + "…";
    }

    private string BuildLedgerAppendix(string? existingMemory, string? existingDaily, ProfileWork profile)
    {
        var userPrompt = new StringBuilder();
        userPrompt.AppendLine();
        if (!string.IsNullOrWhiteSpace(existingMemory))
        {
            userPrompt.AppendLine();
            userPrompt.AppendLine("MEMORY.md (read-only curated long-term memory — do NOT restate):");
            userPrompt.AppendLine(existingMemory);
        }

        if (!string.IsNullOrWhiteSpace(existingDaily))
        {
            userPrompt.AppendLine();
            userPrompt.AppendLine("Today's daily ledger so far (your output will be appended after):");
            userPrompt.AppendLine(existingDaily);
        }

        foreach (var kind in profile.Due)
        {
            var name = ProfileKinds.FileName(kind);
            userPrompt.AppendLine();
            if (profile.Current.TryGetValue(kind, out var existing) && !string.IsNullOrWhiteSpace(existing))
            {
                userPrompt.AppendLine($"Current {name} (merge into this; do NOT blindly append):");
                userPrompt.AppendLine(existing);
            }
            else
            {
                userPrompt.AppendLine($"Current {name} is empty. Write its first version.");
            }
        }

        return userPrompt.ToString();
    }

    internal static bool HasExtractableConversation(IReadOnlyList<ChatMessage> messages)
    {
        foreach (var message in messages)
        {
            if (IsSkippedForMemory(message))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(message.Content))
            {
                return true;
            }
        }

        return false;
    }

    private string SerializeMessages(IReadOnlyList<ChatMessage> messages)
    {
        var sb = new StringBuilder();
        foreach (var message in messages)
        {
            if (IsSkippedForMemory(message))
            {
                continue;
            }

            sb.Append('[').Append(message.Role).Append("]: ");
            sb.AppendLine(message.Content);
            sb.AppendLine();
        }

        var result = sb.ToString();
        var maxChars = Math.Max(1, _cfg.MaxFlushConversationChars);
        if (result.Length > maxChars)
        {
            result = result[^maxChars..];
        }

        return result;
    }

    private static List<ChatMessage> WithoutHandoffNotes(IReadOnlyList<ChatMessage> messages) =>
        messages.Where(message => !SessionHandoffNote.IsHandoffMessage(message)).ToList();

    private static bool IsSkippedForMemory(ChatMessage message)
    {
        if (message.Role is MessageRole.System or MessageRole.Compaction)
        {
            return true;
        }

        if (SessionHandoffNote.IsHandoffMessage(message))
        {
            return true;
        }

        return message.Role == MessageRole.User && message.Content?.Contains("<session_context>") == true;
    }

    /// <summary>Profile state resolved once per flush and reused by prompt building and apply.</summary>
    private sealed record ProfileWork(
        IReadOnlyList<ProfileKind> Due,
        IReadOnlyDictionary<ProfileKind, string> Current,
        IReadOnlyList<CorrectionDetector.CorrectionTrajectory> Corrections)
    {
        public static ProfileWork None { get; } = new(
            Array.Empty<ProfileKind>(),
            new Dictionary<ProfileKind, string>(),
            Array.Empty<CorrectionDetector.CorrectionTrajectory>());
    }
}
