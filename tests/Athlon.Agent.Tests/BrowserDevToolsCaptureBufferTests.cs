using Athlon.Agent.App.Services.Browser;

namespace Athlon.Agent.Tests;

public sealed class BrowserDevToolsCaptureBufferTests
{
    [Fact]
    public void NetworkEvents_MergeIntoSingleEntry()
    {
        var buffer = new BrowserDevToolsCaptureBuffer();
        const string requestId = "req-1";

        buffer.IngestNetworkEvent(
            "Network.requestWillBeSent",
            """
            {
              "requestId": "req-1",
              "type": "XHR",
              "timestamp": 1000.5,
              "request": {
                "url": "https://api.example.com/login",
                "method": "POST",
                "headers": { "Content-Type": "application/json" },
                "postData": "{\"user\":\"a\"}"
              }
            }
            """);

        buffer.IngestNetworkEvent(
            "Network.responseReceived",
            """
            {
              "requestId": "req-1",
              "type": "XHR",
              "response": {
                "url": "https://api.example.com/login",
                "status": 200,
                "mimeType": "application/json",
                "headers": { "content-type": "application/json" }
              }
            }
            """);

        buffer.IngestNetworkEvent(
            "Network.loadingFinished",
            """
            {
              "requestId": "req-1",
              "encodedDataLength": 42
            }
            """);

        Assert.True(buffer.TryGetNetworkState(requestId, out var state));
        Assert.NotNull(state);
        Assert.Equal("POST", state!.Method);
        Assert.Equal("https://api.example.com/login", state.Url);
        Assert.Equal(200, state.Status);
        Assert.Equal("application/json", state.MimeType);
        Assert.True(state.HasRequestBody);
        Assert.Equal("{\"user\":\"a\"}", state.RequestBody);
        Assert.True(state.HasResponseBody);
        Assert.Equal(42, state.ResponseBodyBytes);
        Assert.Equal("application/json", state.RequestHeaders["Content-Type"]);
    }

    [Fact]
    public void NetworkRingBuffer_EvictsOldestEntry()
    {
        var buffer = new BrowserDevToolsCaptureBuffer();

        for (var i = 0; i < 51; i++)
        {
            buffer.IngestNetworkEvent(
                "Network.requestWillBeSent",
                $$"""
                {
                  "requestId": "req-{{i}}",
                  "request": { "url": "https://example.com/{{i}}", "method": "GET" }
                }
                """);
        }

        var result = buffer.ListNetworkEntries(100, null);
        Assert.Equal(50, result.TotalBuffered);
        Assert.DoesNotContain(result.Entries, e => e.RequestId == "req-0");
        Assert.Contains(result.Entries, e => e.RequestId == "req-50");
    }

    [Fact]
    public void NetworkList_FiltersByUrlContains()
    {
        var buffer = new BrowserDevToolsCaptureBuffer();
        buffer.IngestNetworkEvent(
            "Network.requestWillBeSent",
            """
            { "requestId": "a", "request": { "url": "https://api.example.com/a", "method": "GET" } }
            """);
        buffer.IngestNetworkEvent(
            "Network.requestWillBeSent",
            """
            { "requestId": "b", "request": { "url": "https://cdn.example.com/b.js", "method": "GET" } }
            """);

        var result = buffer.ListNetworkEntries(50, "/api.");
        Assert.Single(result.Entries);
        Assert.Equal("a", result.Entries[0].RequestId);
    }

    [Fact]
    public void ConsoleEvents_ParseLogAndException()
    {
        var buffer = new BrowserDevToolsCaptureBuffer();

        buffer.IngestConsoleEvent(
            "Runtime.consoleAPICalled",
            """
            {
              "type": "error",
              "timestamp": 2000.25,
              "args": [ { "type": "string", "value": "boom" } ]
            }
            """);

        buffer.IngestConsoleEvent(
            "Runtime.exceptionThrown",
            """
            {
              "timestamp": 2001.5,
              "exceptionDetails": {
                "text": "Uncaught",
                "url": "https://example.com/app.js",
                "lineNumber": 9,
                "exception": { "description": "TypeError: boom" }
              }
            }
            """);

        var result = buffer.ReadConsoleEntries(10);
        Assert.Equal(2, result.TotalBuffered);
        Assert.Equal("error", result.Entries[0].Level);
        Assert.Equal("boom", result.Entries[0].Message);
        Assert.Equal("exception", result.Entries[1].Level);
        Assert.Contains("TypeError", result.Entries[1].Message, StringComparison.Ordinal);
        Assert.Equal("https://example.com/app.js", result.Entries[1].Url);
        Assert.Equal(9, result.Entries[1].LineNumber);
    }

    [Fact]
    public void LoadingFailed_RecordsErrorText()
    {
        var buffer = new BrowserDevToolsCaptureBuffer();
        buffer.IngestNetworkEvent(
            "Network.loadingFailed",
            """
            {
              "requestId": "bad-1",
              "type": "Document",
              "errorText": "net::ERR_CONNECTION_REFUSED"
            }
            """);

        var entry = buffer.ListNetworkEntries(10, null).Entries.Single();
        Assert.Equal("bad-1", entry.RequestId);
        Assert.Equal("net::ERR_CONNECTION_REFUSED", entry.LoadingError);
    }
}
