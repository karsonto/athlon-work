using System.Text.Json;

namespace Athlon.Agent.Core.Compaction;

public sealed class ToolStormBreaker
{
    private readonly int _windowSize;
    private readonly int _threshold;
    private readonly List<RecentToolCall> _recent = [];

    private static readonly HashSet<string> MutatingToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "file_write",
        "file_edit",
        "execute_command",
        "write",
        "edit",
        "edit_diff",
        "apply_patch",
        "delete",
        "move"
    };

    public ToolStormBreaker(ToolStormSettings settings)
    {
        _windowSize = Math.Max(1, settings.WindowSize);
        _threshold = Math.Max(2, settings.Threshold);
    }

    public bool TryInspect(AgentToolCall call, out string? reason)
    {
        reason = null;
        var args = StableStringify(call.Arguments);
        var readOnly = !MutatingToolNames.Contains(call.Name);

        if (!readOnly)
        {
            ClearReadOnlyEntries();
        }

        var count = _recent.Count(entry =>
            string.Equals(entry.Name, call.Name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(entry.Args, args, StringComparison.Ordinal));

        if (count >= _threshold - 1)
        {
            reason =
                $"{call.Name} was called with identical arguments {count + 1} times in this turn; " +
                "repeat-loop guard suppressed the duplicate. Choose a narrower query or explain why another identical call is needed.";
            return false;
        }

        if (TrySuppressSamePath(call, out reason))
        {
            return false;
        }

        _recent.Add(new RecentToolCall(call.Name, args, readOnly, TryGetPath(call), Describe(call)));
        while (_recent.Count > _windowSize)
        {
            _recent.RemoveAt(0);
        }

        return true;
    }

    private bool TrySuppressSamePath(AgentToolCall call, out string? reason)
    {
        reason = null;
        if (!PathScopedReadTools.Contains(call.Name))
        {
            return false;
        }

        var path = TryGetPath(call);
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var earlier = _recent
            .Where(entry =>
                string.Equals(entry.Name, call.Name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.Path, path, StringComparison.Ordinal))
            .ToList();
        if (earlier.Count < _threshold - 1)
        {
            return false;
        }

        var seen = string.Join("; ", earlier.Select(entry => entry.Detail).Where(detail => !string.IsNullOrWhiteSpace(detail)));
        reason = string.IsNullOrEmpty(seen)
            ? $"{call.Name} already inspected `{path}` {earlier.Count + 1} times in this turn. Use the earlier results instead of reading the same path again."
            : $"{call.Name} already inspected `{path}` {earlier.Count + 1} times in this turn ({seen}). Use the earlier results instead of reading the same path again.";
        return true;
    }

    private static readonly HashSet<string> PathScopedReadTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "file_read",
        "grep_files",
        "glob_files",
        "file_list"
    };

    private static string? TryGetPath(AgentToolCall call)
    {
        var path = call.Arguments.GetString(ToolPathNormalizer.PathArgumentName);
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return StabilizePath(path);
    }

    private static string Describe(AgentToolCall call)
    {
        if (string.Equals(call.Name, "file_read", StringComparison.OrdinalIgnoreCase))
        {
            var start = call.Arguments.TryGetInt32("start_line", out var startLine) ? startLine : 1;
            var end = call.Arguments.TryGetInt32("end_line", out var endLine) ? endLine.ToString() : "open";
            return $"lines {start}-{end}";
        }

        if (string.Equals(call.Name, "grep_files", StringComparison.OrdinalIgnoreCase)
            || string.Equals(call.Name, "glob_files", StringComparison.OrdinalIgnoreCase))
        {
            var pattern = call.Arguments.GetString("pattern");
            return string.IsNullOrWhiteSpace(pattern) ? call.Name : $"pattern {pattern}";
        }

        return "directory listing";
    }

    private void ClearReadOnlyEntries()
    {
        for (var index = _recent.Count - 1; index >= 0; index--)
        {
            if (_recent[index].ReadOnly)
            {
                _recent.RemoveAt(index);
            }
        }
    }

    private static string StableStringify(ToolCallArguments arguments)
    {
        try
        {
            var normalized = arguments
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(
                    pair => pair.Key,
                    pair => NormalizeArgumentValue(pair.Key, pair.Value),
                    StringComparer.Ordinal);
            return JsonSerializer.Serialize(normalized);
        }
        catch
        {
            return string.Join(
                "|",
                arguments
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => $"{pair.Key}={NormalizeArgumentValue(pair.Key, pair.Value)}"));
        }
    }

    private static object NormalizeArgumentValue(string key, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String
            && (string.Equals(key, ToolPathNormalizer.PathArgumentName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, ToolPathNormalizer.CwdArgumentName, StringComparison.OrdinalIgnoreCase)))
        {
            return StabilizePath(value.GetString());
        }

        return value;
    }

    private static string StabilizePath(string? path)
    {
        var normalized = ToolPathNormalizer.ForModel(path ?? string.Empty);
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized;
    }

    private sealed record RecentToolCall(string Name, string Args, bool ReadOnly, string? Path, string? Detail);
}
