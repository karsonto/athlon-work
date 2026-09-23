using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Athlon.Agent.Core.ComputerUse;

namespace Athlon.Agent.Core.Compaction;

public static partial class RequestHistoryHygiene
{
    private const int MaxSignalLines = 48;
    private const int MaxLineChars = 280;
    private const int LongArgumentPreviewChars = 160;

    private static readonly Regex ContinuitySignalLineRe = ContinuitySignalLinePattern();
    private static readonly HashSet<string> ContinuityArgumentNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "path",
        "cwd",
        "start_line",
        "end_line",
        "next_offset",
        "next_start_line",
        "truncated",
        "code",
        "remediation"
    };
    private static readonly Regex Base64KeyRe = Base64KeyPattern();
    private static readonly Regex DataUrlRe = DataUrlPattern();

    /// <summary>
    /// True for argument names hygiene always preserves verbatim (paths, ranges, continuity fields).
    /// Shared with <see cref="ContextTokenEstimator"/> so estimation clamps exactly what hygiene rewrites.
    /// </summary>
    public static bool IsContinuityArgument(string name) => ContinuityArgumentNames.Contains(name);

    public sealed record ApplyResult(IReadOnlyList<AgentModelMessage> Messages, int EstimatedSavingsTokens);

    public sealed record SummaryCompactResult(string Text, int CharsBefore, int CharsAfter, int EstimatedSavingsTokens);

    public static SummaryCompactResult CompactTextForSummary(string text, RequestHistoryHygieneSettings settings)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new SummaryCompactResult(text, 0, 0, 0);
        }

        var beforeChars = text.Length;
        var beforeTokens = ContextTokenEstimator.EstimateTextTokens(text);
        var compacted = CompactToolPayload(text, settings);
        var afterTokens = ContextTokenEstimator.EstimateTextTokens(compacted);
        return new SummaryCompactResult(
            compacted,
            beforeChars,
            compacted.Length,
            Math.Max(0, beforeTokens - afterTokens));
    }

    public static ApplyResult ApplyToModelMessages(
        IReadOnlyList<AgentModelMessage> messages,
        RequestHistoryHygieneSettings settings)
    {
        if (!settings.Enabled || messages.Count == 0)
        {
            return new ApplyResult(messages, 0);
        }

        var beforeTokens = EstimatePayloadTokens(messages);
        var pairedToolCallIds = messages
            .Where(message => string.Equals(message.Role, "tool", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(message.ToolCallId))
            .Select(message => message.ToolCallId!)
            .ToHashSet(StringComparer.Ordinal);

        // Tool messages only carry the call id, so resolve names from the assistant calls that
        // precede them. Needed to apply per-tool result budgets.
        var toolNamesByCallId = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            if (message.ToolCalls is not { Count: > 0 } calls)
            {
                continue;
            }

            foreach (var call in calls)
            {
                if (!string.IsNullOrWhiteSpace(call.Id))
                {
                    toolNamesByCallId[call.Id] = call.Name;
                }
            }
        }

        var changed = false;
        var output = new List<AgentModelMessage>(messages.Count);

        // Historical Computer Use frames are superseded by every newer observation, so their trees
        // are stripped to a summary. Counted newest-first so the newest N stay complete.
        var remainingFullUiTrees = Math.Max(0, settings.HistoryUiTreeRetention);
        var stripUiTreeIndexes = settings.PruneHistoricalUiTree
            ? ResolveUiTreeStripIndexes(messages, toolNamesByCallId, remainingFullUiTrees)
            : null;

        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            if (stripUiTreeIndexes is not null && stripUiTreeIndexes.Contains(index))
            {
                var stripped = StripUiTree(GetTextContent(message.Content));
                if (!string.Equals(stripped, GetTextContent(message.Content), StringComparison.Ordinal))
                {
                    changed = true;
                    output.Add(message with { Content = stripped });
                    continue;
                }
            }

            if (IsToolPayloadMessage(message))
            {
                var toolName = message.ToolCallId is { Length: > 0 } callId
                    && toolNamesByCallId.TryGetValue(callId, out var resolved)
                        ? resolved
                        : null;
                var compacted = CompactToolPayload(
                    GetTextContent(message.Content),
                    settings,
                    settings.ResolveToolResultLimit(toolName));
                if (!string.Equals(compacted, GetTextContent(message.Content), StringComparison.Ordinal))
                {
                    changed = true;
                    output.Add(message with { Content = compacted });
                    continue;
                }
            }
            else if (string.Equals(message.Role, "assistant", StringComparison.Ordinal)
                     && message.ToolCalls is { Count: > 0 })
            {
                var compactedCalls = CompactToolCalls(message.ToolCalls, settings, pairedToolCallIds);
                if (!ReferenceEquals(compactedCalls, message.ToolCalls))
                {
                    changed = true;
                    output.Add(message with { ToolCalls = compactedCalls });
                    continue;
                }
            }

            output.Add(message);
        }

        if (!changed)
        {
            return new ApplyResult(messages, 0);
        }

        var afterTokens = EstimatePayloadTokens(output);
        return new ApplyResult(output, Math.Max(0, beforeTokens - afterTokens));
    }

    /// <summary>
    /// Indexes of Computer Use observations whose <c>ui_tree</c> should be summarized. Walks newest
    /// first so the most recent <paramref name="retention"/> full trees are preserved.
    /// </summary>
    private static HashSet<int> ResolveUiTreeStripIndexes(
        IReadOnlyList<AgentModelMessage> messages,
        Dictionary<string, string> toolNamesByCallId,
        int retention)
    {
        var retained = 0;
        var strip = new HashSet<int>();
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            var message = messages[index];
            if (!IsToolPayloadMessage(message))
            {
                continue;
            }

            var callId = ModelMessageBuilder.ExtractToolCallId(GetTextContent(message.Content));
            if (string.IsNullOrWhiteSpace(callId)
                || !toolNamesByCallId.TryGetValue(callId!, out var toolName)
                || !string.Equals(toolName, ComputerUseToolNames.Observe, StringComparison.Ordinal))
            {
                continue;
            }

            if (!GetTextContent(message.Content).Contains("\"ui_tree\"", StringComparison.Ordinal))
            {
                continue;
            }

            if (retained < retention)
            {
                retained++;
                continue;
            }

            strip.Add(index);
        }

        return strip;
    }

    /// <summary>
    /// Replaces the <c>ui_tree</c> array in an observation payload with a compact summary containing
    /// the frame id, foreground window, cursor, and node count. Everything else (the screenshot
    /// reference and geometry) is preserved so older turns stay interpretable.
    /// </summary>
    internal static string StripUiTree(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return content;
        }

        var marker = "\"ui_tree\":";
        var start = content.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return content;
        }

        var valueStart = start + marker.Length;
        while (valueStart < content.Length && char.IsWhiteSpace(content[valueStart]))
        {
            valueStart++;
        }

        if (valueStart >= content.Length || content[valueStart] != '[')
        {
            return content;
        }

        var end = FindArrayEnd(content, valueStart);
        if (end < 0)
        {
            return content;
        }

        var treeJson = content[valueStart..(end + 1)];
        var nodeCount = ComputerUseUiTreeMetrics.CountNodes(treeJson);
        var summary = $"[ui_tree stripped from history: {nodeCount} node(s) in a superseded frame; "
            + "call computer_observe for the current tree]";
        return string.Concat(
            content.AsSpan(0, valueStart),
            System.Text.Json.JsonSerializer.Serialize(summary),
            content.AsSpan(end + 1));
    }

    /// <summary>
    /// Finds the matching <c>]</c> for the array starting at <paramref name="openIndex"/>, honouring
    /// string literals and escapes so braces inside node names cannot terminate the scan early.
    /// </summary>
    private static int FindArrayEnd(string text, int openIndex)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var index = openIndex; index < text.Length; index++)
        {
            var ch = text[index];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (inString)
            {
                if (ch == '\\')
                {
                    escaped = true;
                }
                else if (ch == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (ch)
            {
                case '"':
                    inString = true;
                    break;
                case '[':
                    depth++;
                    break;
                case ']':
                    depth--;
                    if (depth == 0)
                    {
                        return index;
                    }

                    break;
            }
        }

        return -1;
    }

    private static bool IsToolPayloadMessage(AgentModelMessage message)
    {        if (string.Equals(message.Role, "tool", StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.Equals(message.Role, "user", StringComparison.Ordinal))
        {
            return false;
        }

        var text = GetTextContent(message.Content);
        return text.StartsWith("[Tool output]", StringComparison.Ordinal)
            || text.Contains("ToolCallId:", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<AgentToolCall> CompactToolCalls(
        IReadOnlyList<AgentToolCall> toolCalls,
        RequestHistoryHygieneSettings settings,
        HashSet<string> pairedToolCallIds)
    {
        var changed = false;
        var output = new List<AgentToolCall>(toolCalls.Count);
        foreach (var call in toolCalls)
        {
            if (!pairedToolCallIds.Contains(call.Id))
            {
                output.Add(call);
                continue;
            }

            var compactedArgs = CompactArguments(call.Name, call.Arguments, settings);
            if (!ReferenceEquals(compactedArgs, call.Arguments))
            {
                changed = true;
                output.Add(new AgentToolCall(call.Id, call.Name, compactedArgs));
            }
            else
            {
                output.Add(call);
            }
        }

        return changed ? output : toolCalls;
    }

    private static ToolCallArguments CompactArguments(
        string toolName,
        ToolCallArguments arguments,
        RequestHistoryHygieneSettings settings)
    {
        var changed = false;
        var output = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (key, value) in arguments)
        {
            if (ContinuityArgumentNames.Contains(key) || value.ValueKind != JsonValueKind.String)
            {
                output[key] = value;
                continue;
            }

            var text = value.GetString() ?? string.Empty;
            var bytes = Encoding.UTF8.GetByteCount(text);
            var tokens = ContextTokenEstimator.EstimateTextTokens(text);
            if (bytes <= settings.MaxToolArgumentStringBytes && tokens <= settings.MaxToolArgumentStringTokens)
            {
                output[key] = value;
                continue;
            }

            changed = true;
            var preview = text.Length <= LongArgumentPreviewChars
                ? text
                : text[..LongArgumentPreviewChars].Replace("\n", " ", StringComparison.Ordinal).Trim();
            output[key] = JsonSerializer.SerializeToElement(
                $"[cache hygiene: omitted completed {toolName}.{key} argument, {FormatBytes(bytes)}, approx {tokens} token(s); see following tool result] preview={JsonEscape(preview)}");
        }

        return changed ? new ToolCallArguments(output) : arguments;
    }

    private static string CompactToolPayload(
        string text,
        RequestHistoryHygieneSettings settings,
        ToolResultLimit? limit = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        if (text.Contains("[Tool result evicted", StringComparison.OrdinalIgnoreCase)
            || text.Contains("[cache hygiene:", StringComparison.OrdinalIgnoreCase))
        {
            return CompactEmbeddedBase64(text);
        }

        var effective = limit ?? new ToolResultLimit(
            settings.MaxToolResultLines,
            settings.MaxToolResultBytes,
            settings.MaxToolResultTokens);
        var originalBytes = Encoding.UTF8.GetByteCount(text);
        var originalLines = CountLines(text);
        var originalTokens = ContextTokenEstimator.EstimateTextTokens(text);
        if (originalBytes <= effective.MaxBytes
            && originalLines <= effective.MaxLines
            && originalTokens <= effective.MaxTokens)
        {
            return CompactEmbeddedBase64(text);
        }

        var normalized = NormalizeTextBlock(text);
        var lines = normalized.Split('\n');
        var selected = SelectUsefulLines(lines, effective.MaxLines);
        var marker =
            $"[cache hygiene: omitted {Math.Max(0, lines.Length - selected.Count)} line(s); use narrower read/grep/execute_command ranges for details]";
        var fitted = FitLinesToBudget(
            selected.Select(CompactLine).ToList(),
            effective.MaxBytes - Encoding.UTF8.GetByteCount(marker) - 1,
            effective.MaxTokens - ContextTokenEstimator.EstimateTextTokens(marker) - 1);
        return string.Join('\n', fitted.Append(marker));
    }

    private static string CompactEmbeddedBase64(string text)
    {
        var lines = text.Split('\n');
        var changed = false;
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (ShouldOmitBase64(line))
            {
                lines[index] = $"[cache hygiene: omitted base64 data, {FormatBytes(Encoding.UTF8.GetByteCount(line))}]";
                changed = true;
            }
        }

        return changed ? string.Join('\n', lines) : text;
    }

    private static bool ShouldOmitBase64(string value) =>
        value.Length > 256 && (DataUrlRe.IsMatch(value) || value.All(static ch => char.IsLetterOrDigit(ch) || ch is '+' or '/' or '='));

    private static List<string> SelectUsefulLines(string[] lines, int maxLines)
    {
        if (lines.Length <= maxLines)
        {
            return lines.ToList();
        }

        var mandatoryIndexes = new List<int>();
        for (var index = 0; index < lines.Length && mandatoryIndexes.Count < MaxSignalLines; index++)
        {
            if (ContinuitySignalLineRe.IsMatch(lines[index]))
            {
                mandatoryIndexes.Add(index);
            }
        }

        var indexes = mandatoryIndexes.ToHashSet();
        var headCount = Math.Min(80, Math.Max(1, (int)Math.Floor(maxLines * 0.25)));
        var tailCount = Math.Min(120, Math.Max(1, (int)Math.Floor(maxLines * 0.35)));
        for (var index = 0; index < Math.Min(headCount, lines.Length) && indexes.Count < maxLines; index++)
        {
            indexes.Add(index);
        }

        for (var index = Math.Max(0, lines.Length - tailCount); index < lines.Length && indexes.Count < maxLines; index++)
        {
            indexes.Add(index);
        }

        return mandatoryIndexes
            .Concat(indexes.Except(mandatoryIndexes).OrderBy(index => index))
            .Take(maxLines)
            .Select(index => lines[index])
            .ToList();
    }

    private static List<string> FitLinesToBudget(List<string> lines, int maxBytes, int maxTokens)
    {
        var output = new List<string>();
        var bytes = 0;
        var tokens = 0;
        foreach (var line in lines)
        {
            var lineBytes = Encoding.UTF8.GetByteCount(line) + (output.Count > 0 ? 1 : 0);
            var lineTokens = ContextTokenEstimator.EstimateTextTokens(line) + (output.Count > 0 ? 1 : 0);
            if (bytes + lineBytes > maxBytes || tokens + lineTokens > maxTokens)
            {
                break;
            }

            output.Add(line);
            bytes += lineBytes;
            tokens += lineTokens;
        }

        if (output.Count == 0 && lines.Count > 0)
        {
            output.Add(lines[0][..Math.Min(lines[0].Length, MaxLineChars)]);
        }

        return output;
    }

    private static string NormalizeTextBlock(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').Select(line => line.TrimEnd()).ToList();
        var output = new List<string>();
        var blankRun = 0;
        var previous = string.Empty;
        var repeatCount = 0;

        void FlushRepeat()
        {
            if (repeatCount > 1)
            {
                output.Add($"[previous line repeated {repeatCount - 1} time(s)]");
            }

            repeatCount = 0;
        }

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                FlushRepeat();
                blankRun++;
                if (blankRun <= 2)
                {
                    output.Add(string.Empty);
                }

                previous = string.Empty;
                continue;
            }

            blankRun = 0;
            if (line == previous)
            {
                repeatCount++;
                continue;
            }

            FlushRepeat();
            output.Add(line);
            previous = line;
            repeatCount = 1;
        }

        FlushRepeat();
        return string.Join('\n', output).Trim();
    }

    private static string CompactLine(string line)
    {
        if (line.Length <= MaxLineChars)
        {
            return line;
        }

        var head = (int)Math.Floor(MaxLineChars * 0.6);
        var tail = Math.Max(0, MaxLineChars - head - 5);
        return $"{line[..head].TrimEnd()} ... {line[^tail..].TrimStart()}";
    }

    internal static int EstimatePayloadTokens(IReadOnlyList<AgentModelMessage> messages)
    {
        var total = 0;
        foreach (var message in messages)
        {
            total += ContextTokenEstimator.EstimateTextTokens(GetTextContent(message.Content));
            if (message.ToolCalls is { Count: > 0 })
            {
                foreach (var call in message.ToolCalls)
                {
                    total += ContextTokenEstimator.EstimateTextTokens(call.Name);
                    foreach (var argument in call.Arguments)
                    {
                        total += ContextTokenEstimator.EstimateTextTokens(argument.Key);
                        total += ContextTokenEstimator.EstimateTextTokens(argument.Value.GetRawText());
                    }
                }
            }
        }

        return total;
    }

    private static int CountLines(string text) => string.IsNullOrEmpty(text) ? 0 : text.Split('\n').Length;

    private static string GetTextContent(object content) =>
        content switch
        {
            string text => text,
            _ => content.ToString() ?? string.Empty
        };

    private static string FormatBytes(int bytes) =>
        bytes < 1024 ? $"{bytes}B" : bytes < 1024 * 1024 ? $"{bytes / 1024.0:F1}KB" : $"{bytes / (1024.0 * 1024.0):F1}MB";

    private static string JsonEscape(string value) => JsonSerializer.Serialize(value);

    [GeneratedRegex(@"(?:[""']?(?:path|cwd|start_line|end_line|next_offset|next_start_line|truncated|code|remediation)[""']?\s*[:=])|\b(error|failed?|fatal|panic|exception|traceback|warning|warn|denied|timeout|timed out|not found|cannot|invalid)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ContinuitySignalLinePattern();

    [GeneratedRegex(@"(?:^|_)(?:data_)?base64$", RegexOptions.IgnoreCase)]
    private static partial Regex Base64KeyPattern();

    [GeneratedRegex(@"^data:[^;,]+;base64,", RegexOptions.IgnoreCase)]
    private static partial Regex DataUrlPattern();
}
