using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Athlon.Agent.Core.Compaction;

public static partial class RequestHistoryHygiene
{
    private const int MaxSignalLines = 48;
    private const int MaxLineChars = 280;
    private const int LongArgumentPreviewChars = 160;

    private static readonly Regex SignalLineRe = SignalLinePattern();
    private static readonly Regex Base64KeyRe = Base64KeyPattern();
    private static readonly Regex DataUrlRe = DataUrlPattern();

    public sealed record ApplyResult(IReadOnlyList<AgentModelMessage> Messages, int EstimatedSavingsTokens);

    public static ApplyResult ApplyToModelMessages(
        IReadOnlyList<AgentModelMessage> messages,
        RequestHistoryHygieneSettings settings)
    {
        if (!settings.Enabled || messages.Count == 0)
        {
            return new ApplyResult(messages, 0);
        }

        var beforeTokens = EstimateMessagesTokens(messages);
        var pairedToolCallIds = messages
            .Where(message => string.Equals(message.Role, "tool", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(message.ToolCallId))
            .Select(message => message.ToolCallId!)
            .ToHashSet(StringComparer.Ordinal);

        var changed = false;
        var output = new List<AgentModelMessage>(messages.Count);
        foreach (var message in messages)
        {
            if (IsToolPayloadMessage(message))
            {
                var compacted = CompactToolPayload(GetTextContent(message.Content), settings);
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

        var afterTokens = EstimateMessagesTokens(output);
        return new ApplyResult(output, Math.Max(0, beforeTokens - afterTokens));
    }

    private static bool IsToolPayloadMessage(AgentModelMessage message)
    {
        if (string.Equals(message.Role, "tool", StringComparison.Ordinal))
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

    private static IReadOnlyDictionary<string, string> CompactArguments(
        string toolName,
        IReadOnlyDictionary<string, string> arguments,
        RequestHistoryHygieneSettings settings)
    {
        var changed = false;
        var output = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in arguments)
        {
            var bytes = Encoding.UTF8.GetByteCount(value);
            var tokens = EstimateTokens(value);
            if (bytes <= settings.MaxToolArgumentStringBytes && tokens <= settings.MaxToolArgumentStringTokens)
            {
                output[key] = value;
                continue;
            }

            changed = true;
            var preview = value.Length <= LongArgumentPreviewChars
                ? value
                : value[..LongArgumentPreviewChars].Replace("\n", " ", StringComparison.Ordinal).Trim();
            output[key] =
                $"[cache hygiene: omitted completed {toolName}.{key} argument, {FormatBytes(bytes)}, approx {tokens} token(s); see following tool result] preview={JsonEscape(preview)}";
        }

        return changed ? output : arguments;
    }

    private static string CompactToolPayload(string text, RequestHistoryHygieneSettings settings)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var originalBytes = Encoding.UTF8.GetByteCount(text);
        var originalLines = CountLines(text);
        var originalTokens = EstimateTokens(text);
        if (originalBytes <= settings.MaxToolResultBytes
            && originalLines <= settings.MaxToolResultLines
            && originalTokens <= settings.MaxToolResultTokens)
        {
            return CompactEmbeddedBase64(text);
        }

        var normalized = NormalizeTextBlock(text);
        var lines = normalized.Split('\n');
        var selected = SelectUsefulLines(lines, settings.MaxToolResultLines);
        var marker =
            $"[cache hygiene: omitted {Math.Max(0, lines.Length - selected.Count)} line(s); use narrower read/grep/execute_command ranges for details]";
        var fitted = FitLinesToBudget(
            selected.Select(CompactLine).ToList(),
            settings.MaxToolResultBytes - Encoding.UTF8.GetByteCount(marker) - 1,
            settings.MaxToolResultTokens - EstimateTokens(marker) - 1);
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

        var indexes = new HashSet<int>();
        var headCount = Math.Min(80, Math.Max(1, (int)Math.Floor(maxLines * 0.25)));
        var tailCount = Math.Min(120, Math.Max(1, (int)Math.Floor(maxLines * 0.35)));
        for (var index = 0; index < Math.Min(headCount, lines.Length); index++)
        {
            indexes.Add(index);
        }

        for (var index = Math.Max(0, lines.Length - tailCount); index < lines.Length; index++)
        {
            indexes.Add(index);
        }

        var signalCount = 0;
        for (var index = 0; index < lines.Length && indexes.Count < maxLines; index++)
        {
            if (!SignalLineRe.IsMatch(lines[index]))
            {
                continue;
            }

            indexes.Add(index);
            signalCount++;
            if (signalCount >= MaxSignalLines)
            {
                break;
            }
        }

        return indexes.OrderBy(index => index).Take(maxLines).Select(index => lines[index]).ToList();
    }

    private static List<string> FitLinesToBudget(List<string> lines, int maxBytes, int maxTokens)
    {
        var output = new List<string>();
        var bytes = 0;
        var tokens = 0;
        foreach (var line in lines)
        {
            var lineBytes = Encoding.UTF8.GetByteCount(line) + (output.Count > 0 ? 1 : 0);
            var lineTokens = EstimateTokens(line) + (output.Count > 0 ? 1 : 0);
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

    private static int EstimateMessagesTokens(IReadOnlyList<AgentModelMessage> messages)
    {
        var total = 0;
        foreach (var message in messages)
        {
            total += EstimateTokens(GetTextContent(message.Content));
            if (message.ToolCalls is { Count: > 0 })
            {
                foreach (var call in message.ToolCalls)
                {
                    total += EstimateTokens(call.Name);
                    foreach (var argument in call.Arguments)
                    {
                        total += EstimateTokens(argument.Key);
                        total += EstimateTokens(argument.Value);
                    }
                }
            }
        }

        return total;
    }

    private static int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        return Math.Max(1, (int)Math.Ceiling(text.Length / 4.0));
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

    [GeneratedRegex(@"\b(error|failed?|fatal|panic|exception|traceback|warning|warn|denied|timeout|timed out|not found|cannot|invalid)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SignalLinePattern();

    [GeneratedRegex(@"(?:^|_)(?:data_)?base64$", RegexOptions.IgnoreCase)]
    private static partial Regex Base64KeyPattern();

    [GeneratedRegex(@"^data:[^;,]+;base64,", RegexOptions.IgnoreCase)]
    private static partial Regex DataUrlPattern();
}
