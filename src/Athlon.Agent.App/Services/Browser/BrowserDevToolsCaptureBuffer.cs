using System.Text;
using System.Text.Json;
using Athlon.Agent.Core.Browser;

namespace Athlon.Agent.App.Services.Browser;

/// <summary>In-memory ring buffers for CDP network and console events (testable without WebView2).</summary>
public sealed class BrowserDevToolsCaptureBuffer
{
    public const int DefaultMaxNetworkEntries = 50;
    public const int DefaultMaxConsoleEntries = 100;

    private readonly object _gate = new();
    private readonly List<string> _networkOrder = [];
    private readonly Dictionary<string, NetworkEntryState> _networkEntries = new(StringComparer.Ordinal);
    private readonly List<BrowserConsoleEntry> _consoleEntries = [];

    public void IngestNetworkEvent(string eventName, string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        lock (_gate)
        {
            switch (eventName)
            {
                case "Network.requestWillBeSent":
                    IngestRequestWillBeSent(root);
                    break;
                case "Network.responseReceived":
                    IngestResponseReceived(root);
                    break;
                case "Network.loadingFinished":
                    IngestLoadingFinished(root);
                    break;
                case "Network.loadingFailed":
                    IngestLoadingFailed(root);
                    break;
            }
        }
    }

    public void IngestConsoleEvent(string eventName, string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        BrowserConsoleEntry? entry = eventName switch
        {
            "Runtime.consoleAPICalled" => ParseConsoleApiCalled(root),
            "Runtime.exceptionThrown" => ParseExceptionThrown(root),
            _ => null
        };

        if (entry is null)
        {
            return;
        }

        lock (_gate)
        {
            _consoleEntries.Add(entry);
            TrimConsoleEntries();
        }
    }

    public BrowserNetworkListResult ListNetworkEntries(int limit, string? urlContains)
    {
        if (limit <= 0)
        {
            limit = DefaultMaxNetworkEntries;
        }

        lock (_gate)
        {
            var total = _networkOrder.Count;
            IEnumerable<string> ids = _networkOrder;
            if (!string.IsNullOrWhiteSpace(urlContains))
            {
                ids = ids.Where(id =>
                    _networkEntries.TryGetValue(id, out var entry)
                    && entry.Url.Contains(urlContains, StringComparison.OrdinalIgnoreCase));
            }

            var summaries = ids
                .Select(id => _networkEntries.TryGetValue(id, out var entry) ? entry.ToSummary() : null)
                .Where(summary => summary is not null)
                .Cast<BrowserNetworkEntrySummary>()
                .TakeLast(limit)
                .ToArray();

            return new BrowserNetworkListResult(summaries, total);
        }
    }

    public bool TryGetNetworkState(string requestId, out NetworkEntryState? state)
    {
        lock (_gate)
        {
            if (_networkEntries.TryGetValue(requestId, out var entry))
            {
                state = entry;
                return true;
            }
        }

        state = null;
        return false;
    }

    public BrowserConsoleReadResult ReadConsoleEntries(int limit)
    {
        if (limit <= 0)
        {
            limit = DefaultMaxConsoleEntries;
        }

        lock (_gate)
        {
            var total = _consoleEntries.Count;
            var entries = _consoleEntries.TakeLast(limit).ToArray();
            return new BrowserConsoleReadResult(entries, total);
        }
    }

    private void IngestRequestWillBeSent(JsonElement root)
    {
        var requestId = GetString(root, "requestId");
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return;
        }

        if (!_networkEntries.ContainsKey(requestId))
        {
            AddNetworkEntry(requestId);
        }

        var entry = _networkEntries[requestId];
        entry.TimestampMs = ToTimestampMs(root, "timestamp", "wallTime");
        entry.ResourceType = GetString(root, "type") ?? entry.ResourceType;

        if (root.TryGetProperty("request", out var request))
        {
            entry.Method = GetString(request, "method") ?? entry.Method;
            entry.Url = GetString(request, "url") ?? entry.Url;
            entry.RequestBody = GetString(request, "postData") ?? entry.RequestBody;
            entry.RequestHeaders = ParseHeaders(request, "headers");
            entry.HasRequestBody = !string.IsNullOrEmpty(entry.RequestBody);
        }
    }

    private void IngestResponseReceived(JsonElement root)
    {
        var requestId = GetString(root, "requestId");
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return;
        }

        if (!_networkEntries.ContainsKey(requestId))
        {
            AddNetworkEntry(requestId);
        }

        var entry = _networkEntries[requestId];
        entry.ResourceType = GetString(root, "type") ?? entry.ResourceType;

        if (root.TryGetProperty("response", out var response))
        {
            entry.Status = response.TryGetProperty("status", out var statusEl) && statusEl.TryGetInt32(out var status)
                ? status
                : entry.Status;
            entry.MimeType = GetString(response, "mimeType") ?? entry.MimeType;
            entry.ResponseHeaders = ParseHeaders(response, "headers");
            entry.Url = GetString(response, "url") ?? entry.Url;
        }
    }

    private void IngestLoadingFinished(JsonElement root)
    {
        var requestId = GetString(root, "requestId");
        if (string.IsNullOrWhiteSpace(requestId) || !_networkEntries.TryGetValue(requestId, out var entry))
        {
            return;
        }

        entry.HasResponseBody = true;
        if (root.TryGetProperty("encodedDataLength", out var lenEl) && lenEl.TryGetInt64(out var len))
        {
            entry.ResponseBodyBytes = (int)Math.Min(len, int.MaxValue);
        }
    }

    private void IngestLoadingFailed(JsonElement root)
    {
        var requestId = GetString(root, "requestId");
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return;
        }

        if (!_networkEntries.ContainsKey(requestId))
        {
            AddNetworkEntry(requestId);
        }

        var entry = _networkEntries[requestId];
        entry.LoadingError = GetString(root, "errorText") ?? entry.LoadingError;
        entry.ResourceType = GetString(root, "type") ?? entry.ResourceType;
    }

    private void AddNetworkEntry(string requestId)
    {
        _networkOrder.Add(requestId);
        _networkEntries[requestId] = new NetworkEntryState { RequestId = requestId };
        TrimNetworkEntries();
    }

    private void TrimNetworkEntries()
    {
        while (_networkOrder.Count > DefaultMaxNetworkEntries)
        {
            var oldest = _networkOrder[0];
            _networkOrder.RemoveAt(0);
            _networkEntries.Remove(oldest);
        }
    }

    private void TrimConsoleEntries()
    {
        var overflow = _consoleEntries.Count - DefaultMaxConsoleEntries;
        if (overflow > 0)
        {
            _consoleEntries.RemoveRange(0, overflow);
        }
    }

    private static BrowserConsoleEntry? ParseConsoleApiCalled(JsonElement root)
    {
        var level = GetString(root, "type") ?? "log";
        var message = FormatConsoleArgs(root);
        var timestampMs = ToTimestampMs(root, "timestamp");
        var stackTrace = FormatStackTrace(root.TryGetProperty("stackTrace", out var st) ? st : default);
        var (url, line) = ExtractTopFrame(root.TryGetProperty("stackTrace", out var stack) ? stack : default);

        return new BrowserConsoleEntry(timestampMs, level, message, stackTrace, url, line);
    }

    private static BrowserConsoleEntry? ParseExceptionThrown(JsonElement root)
    {
        if (!root.TryGetProperty("exceptionDetails", out var details))
        {
            return null;
        }

        var message = GetString(details, "text") ?? "Exception";
        if (details.TryGetProperty("exception", out var exception))
        {
            var description = GetString(exception, "description");
            if (!string.IsNullOrWhiteSpace(description))
            {
                message = description;
            }
        }

        var timestampMs = ToTimestampMs(root, "timestamp");
        var stackTrace = FormatStackTrace(details.TryGetProperty("stackTrace", out var st) ? st : default);
        var url = GetString(details, "url");
        int? line = details.TryGetProperty("lineNumber", out var lineEl) && lineEl.TryGetInt32(out var lineNum)
            ? lineNum
            : null;

        return new BrowserConsoleEntry(timestampMs, "exception", message, stackTrace, url, line);
    }

    private static string FormatConsoleArgs(JsonElement root)
    {
        if (!root.TryGetProperty("args", out var args) || args.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        foreach (var arg in args.EnumerateArray())
        {
            parts.Add(FormatRemoteObject(arg));
        }

        return string.Join(" ", parts);
    }

    private static string FormatRemoteObject(JsonElement arg)
    {
        var type = GetString(arg, "type");
        if (type == "string" && arg.TryGetProperty("value", out var valueEl))
        {
            return valueEl.ValueKind == JsonValueKind.String
                ? valueEl.GetString() ?? string.Empty
                : valueEl.ToString();
        }

        if (type == "undefined")
        {
            return "undefined";
        }

        if (arg.TryGetProperty("description", out var descEl) && descEl.ValueKind == JsonValueKind.String)
        {
            return descEl.GetString() ?? arg.ToString();
        }

        if (arg.TryGetProperty("value", out var val))
        {
            return val.ToString();
        }

        return arg.ToString();
    }

    private static string? FormatStackTrace(JsonElement stackTrace)
    {
        if (stackTrace.ValueKind != JsonValueKind.Object
            || !stackTrace.TryGetProperty("callFrames", out var frames)
            || frames.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var builder = new StringBuilder();
        foreach (var frame in frames.EnumerateArray())
        {
            var fn = GetString(frame, "functionName") ?? "(anonymous)";
            var url = GetString(frame, "url") ?? "";
            var line = frame.TryGetProperty("lineNumber", out var lineEl) && lineEl.TryGetInt32(out var ln)
                ? ln + 1
                : 0;
            builder.Append("    at ").Append(fn).Append(" (").Append(url).Append(':').Append(line).AppendLine(")");
        }

        var text = builder.ToString().TrimEnd();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static (string? Url, int? LineNumber) ExtractTopFrame(JsonElement stackTrace)
    {
        if (stackTrace.ValueKind != JsonValueKind.Object
            || !stackTrace.TryGetProperty("callFrames", out var frames)
            || frames.ValueKind != JsonValueKind.Array)
        {
            return (null, null);
        }

        foreach (var frame in frames.EnumerateArray())
        {
            var url = GetString(frame, "url");
            if (frame.TryGetProperty("lineNumber", out var lineEl) && lineEl.TryGetInt32(out var line))
            {
                return (url, line + 1);
            }
        }

        return (null, null);
    }

    private static Dictionary<string, string> ParseHeaders(JsonElement parent, string propertyName)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!parent.TryGetProperty(propertyName, out var headers))
        {
            return result;
        }

        if (headers.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in headers.EnumerateObject())
            {
                result[prop.Name] = prop.Value.GetString() ?? string.Empty;
            }
        }

        return result;
    }

    private static long ToTimestampMs(JsonElement root, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            if (!root.TryGetProperty(name, out var el))
            {
                continue;
            }

            if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var value))
            {
                // CDP wallTime is seconds since epoch; timestamp is monotonic seconds.
                return name.Contains("wall", StringComparison.OrdinalIgnoreCase)
                    ? (long)(value * 1000)
                    : (long)(value * 1000);
            }
        }

        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
}

public sealed class NetworkEntryState
{
    public required string RequestId { get; init; }

    public string Method { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public int? Status { get; set; }

    public string? ResourceType { get; set; }

    public string? MimeType { get; set; }

    public long TimestampMs { get; set; }

    public string? RequestBody { get; set; }

    public Dictionary<string, string> RequestHeaders { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> ResponseHeaders { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool HasRequestBody { get; set; }

    public bool HasResponseBody { get; set; }

    public int? ResponseBodyBytes { get; set; }

    public string? LoadingError { get; set; }

    public BrowserNetworkEntrySummary ToSummary() =>
        new(
            RequestId,
            Method,
            Url,
            Status,
            ResourceType,
            MimeType,
            TimestampMs,
            HasRequestBody,
            HasResponseBody,
            ResponseBodyBytes,
            LoadingError);
}
