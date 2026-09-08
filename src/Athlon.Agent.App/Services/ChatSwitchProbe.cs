using System.Collections;
using System.IO;
using System.Text.Json;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;

namespace Athlon.Agent.App.Services;

/// <summary>
/// Temporary observation probe for session-switch / timeline replay races.
/// Logs go to StartupTrace and append NDJSON to debug-chat-switch.log.
/// </summary>
internal static class ChatSwitchProbe
{
    private static readonly object Gate = new();
    private static readonly string[] LogPaths =
    [
        Path.Combine(@"f:\athlon-work", "debug-chat-switch.log"),
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Athlon.Agent",
            "debug-chat-switch.log")
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static void Log(string location, string message, object? data = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["location"] = location,
            ["message"] = message,
            ["data"] = data
        };

        string line;
        try
        {
            line = JsonSerializer.Serialize(payload, JsonOptions);
        }
        catch
        {
            line = $"{{\"location\":\"{location}\",\"message\":\"{message}\"}}";
        }

        App.StartupTrace($"[chat-switch] {location} | {message} | {Summarize(data)}");

        lock (Gate)
        {
            foreach (var path in LogPaths)
            {
                try
                {
                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrWhiteSpace(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    File.AppendAllText(path, line + Environment.NewLine);
                }
                catch
                {
                    // Best-effort probe only.
                }
            }
        }
    }

    public static object SummarizeMessages(IReadOnlyList<ChatMessageViewModel>? messages)
    {
        if (messages is null || messages.Count == 0)
        {
            return new { count = 0, roles = Array.Empty<string>(), firstId = (string?)null, lastId = (string?)null };
        }

        var roles = messages
            .GroupBy(m => RoleLabel(m))
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key}:{g.Count()}")
            .ToArray();
        return new
        {
            count = messages.Count,
            roles,
            firstId = ShortId(messages[0].MessageId),
            lastId = ShortId(messages[^1].MessageId),
            lastRole = RoleLabel(messages[^1])
        };
    }

    public static object SummarizeChatMessages(IReadOnlyList<ChatMessage>? messages)
    {
        if (messages is null || messages.Count == 0)
        {
            return new { count = 0, roles = Array.Empty<string>(), firstId = (string?)null, lastId = (string?)null };
        }

        var roles = messages
            .GroupBy(m => m.Role.ToString())
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key}:{g.Count()}")
            .ToArray();
        return new
        {
            count = messages.Count,
            roles,
            firstId = ShortId(messages[0].Id),
            lastId = ShortId(messages[^1].Id),
            lastRole = messages[^1].Role.ToString()
        };
    }

    public static string ShortId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return "";
        }

        return id.Length <= 12 ? id : id[..12];
    }

    private static string RoleLabel(ChatMessageViewModel message)
    {
        if (message.IsUser) return "user";
        if (message.IsTool) return "tool";
        if (message.IsCompaction) return "compaction";
        if (message.AssistantTone) return "assistant";
        return "other";
    }

    private static string Summarize(object? data)
    {
        if (data is null)
        {
            return "";
        }

        try
        {
            if (data is IDictionary dict && dict.Count == 0)
            {
                return "{}";
            }

            var json = JsonSerializer.Serialize(data, JsonOptions);
            return json.Length <= 400 ? json : json[..400] + "…";
        }
        catch
        {
            return data.ToString() ?? "";
        }
    }
}
