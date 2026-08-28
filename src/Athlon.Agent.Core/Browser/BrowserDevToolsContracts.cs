namespace Athlon.Agent.Core.Browser;

public sealed record BrowserNetworkEntrySummary(
    string RequestId,
    string Method,
    string Url,
    int? Status,
    string? ResourceType,
    string? MimeType,
    long TimestampMs,
    bool HasRequestBody,
    bool HasResponseBody,
    int? ResponseBodyBytes,
    string? LoadingError);

public sealed record BrowserNetworkEntryDetail(
    BrowserNetworkEntrySummary Summary,
    IReadOnlyDictionary<string, string> RequestHeaders,
    string? RequestBody,
    IReadOnlyDictionary<string, string> ResponseHeaders,
    string? ResponseBody,
    bool ResponseBodyIsBase64,
    string? ResponseBodyError);

public sealed record BrowserConsoleEntry(
    long TimestampMs,
    string Level,
    string Message,
    string? StackTrace,
    string? Url,
    int? LineNumber);

public sealed record BrowserNetworkListResult(
    IReadOnlyList<BrowserNetworkEntrySummary> Entries,
    int TotalBuffered);

public sealed record BrowserConsoleReadResult(
    IReadOnlyList<BrowserConsoleEntry> Entries,
    int TotalBuffered);
