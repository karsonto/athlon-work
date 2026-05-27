using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Athlon.Agent.Mcp;

public enum McpConnectionState
{
    Disabled,
    Connecting,
    Connected,
    Error
}

public sealed record McpTool(string Name, string Description, string InputSchemaJson);

public sealed record McpServerStatus(
    string Name,
    McpConnectionState State,
    string Transport,
    IReadOnlyList<McpTool> Tools,
    string? LastError = null);

public interface IMcpClient : IAsyncDisposable
{
    string Name { get; }
    McpServerStatus Status { get; }

    Task InitializeAsync(string? clientName = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<McpTool>> ListToolsAsync(CancellationToken cancellationToken = default);
    Task<string> CallToolAsync(string toolName, string argumentsJson, CancellationToken cancellationToken = default);
}

/// <summary>
/// MCP stdio JSON-RPC client. Sends line-delimited JSON-RPC messages over stdin/stdout.
/// Supports initialize, tools/list, tools/call.
/// </summary>
public sealed class McpStdioClient : IMcpClient
{
    private readonly ProcessStartInfo _startInfo;
    private readonly TimeSpan _defaultTimeout;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private Process? _process;
    private StreamWriter? _stdin;
    private Task? _readLoop;
    private long _nextId;
    private volatile bool _disposed;

    private McpConnectionState _state = McpConnectionState.Disabled;
    private List<McpTool> _tools = new();
    private string? _lastError;

    public McpStdioClient(string name, ProcessStartInfo startInfo, TimeSpan? defaultTimeout = null)
    {
        Name = name;
        _startInfo = startInfo;
        _startInfo.RedirectStandardInput = true;
        _startInfo.RedirectStandardOutput = true;
        _startInfo.RedirectStandardError = true;
        _startInfo.UseShellExecute = false;
        _startInfo.CreateNoWindow = true;
        _defaultTimeout = defaultTimeout ?? TimeSpan.FromSeconds(30);
    }

    public string Name { get; }

    public McpServerStatus Status => new(
        Name,
        _state,
        Transport: "stdio",
        Tools: _tools.ToArray(),
        LastError: _lastError);

    public async Task InitializeAsync(string? clientName = null, CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken);

        _state = McpConnectionState.Connecting;
        try
        {
            // Minimal initialize payload; servers that need richer capabilities can still accept this.
            var payload = new JsonObject
            {
                ["clientInfo"] = new JsonObject
                {
                    ["name"] = clientName ?? "Athlon.Agent",
                    ["version"] = "0"
                }
            };
            _ = await SendRequestAsync("initialize", payload, cancellationToken);

            _state = McpConnectionState.Connected;
            _lastError = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _state = McpConnectionState.Error;
            _lastError = ex.Message;
            throw;
        }
    }

    public async Task<IReadOnlyList<McpTool>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken);

        var result = await SendRequestAsync("tools/list", payload: null, cancellationToken);
        var parsed = McpToolParser.ParseTools(result);
        _tools = parsed.ToList();
        return parsed;
    }

    public async Task<string> CallToolAsync(string toolName, string argumentsJson, CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken);

        var argsNode = McpToolParser.ParseArgumentsNode(argumentsJson);

        var payload = new JsonObject
        {
            ["name"] = toolName,
            ["arguments"] = argsNode ?? new JsonObject()
        };

        var result = await SendRequestAsync("tools/call", payload, cancellationToken);
        return result.GetRawText();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            if (_process is not null && !_process.HasExited)
            {
                try { _stdin?.Close(); } catch { /* ignore */ }
                _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // ignore dispose failures
        }

        foreach (var entry in _pending)
        {
            entry.Value.TrySetException(new ObjectDisposedException(nameof(McpStdioClient)));
        }

        _pending.Clear();
        _process?.Dispose();
        _startLock.Dispose();
        _writeLock.Dispose();

        if (_readLoop is not null)
        {
            try { await _readLoop; } catch { /* ignore */ }
        }
    }

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(McpStdioClient));
        }

        if (_process is not null && !_process.HasExited && _stdin is not null && _readLoop is not null)
        {
            return;
        }

        await _startLock.WaitAsync(cancellationToken);
        try
        {
            if (_process is not null && !_process.HasExited && _stdin is not null && _readLoop is not null)
            {
                return;
            }

            _process?.Dispose();
            _process = Process.Start(_startInfo) ?? throw new InvalidOperationException($"Failed to start MCP server process for '{Name}'.");
            _stdin = _process.StandardInput;
            _stdin.AutoFlush = true;

            _readLoop = Task.Run(() => ReadLoopAsync(_process.StandardOutput, _process.StandardError, CancellationToken.None));
            _state = McpConnectionState.Connecting;
            _lastError = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _state = McpConnectionState.Error;
            _lastError = ex.Message;
            throw;
        }
        finally
        {
            _startLock.Release();
        }
    }

    private async Task<JsonElement> SendRequestAsync(string method, JsonNode? payload, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_defaultTimeout);
        using var reg = timeoutCts.Token.Register(() =>
        {
            if (_pending.TryRemove(id, out var pending))
            {
                pending.TrySetException(new TimeoutException($"MCP request timed out: {method} (server={Name})"));
            }
        });

        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method
        };
        if (payload is not null)
        {
            request["params"] = payload;
        }

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            if (_stdin is null)
            {
                throw new InvalidOperationException($"MCP stdin is not available for '{Name}'.");
            }

            await _stdin.WriteLineAsync(request.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        }
        finally
        {
            _writeLock.Release();
        }

        return await tcs.Task;
    }

    private async Task ReadLoopAsync(StreamReader stdout, StreamReader stderr, CancellationToken cancellationToken)
    {
        try
        {
            // Best-effort stderr pump to capture last error context.
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!stderr.EndOfStream && !cancellationToken.IsCancellationRequested)
                    {
                        var line = await stderr.ReadLineAsync(cancellationToken);
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            _lastError = line;
                        }
                    }
                }
                catch
                {
                    // ignore
                }
            }, cancellationToken);

            while (!stdout.EndOfStream && !cancellationToken.IsCancellationRequested)
            {
                var line = await stdout.ReadLineAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(line);
                }
                catch
                {
                    continue;
                }

                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (root.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var id))
                {
                    if (_pending.TryRemove(id, out var tcs))
                    {
                        if (root.TryGetProperty("error", out var errEl))
                        {
                            tcs.TrySetException(new InvalidOperationException($"MCP error: {errEl.GetRawText()}"));
                        }
                        else if (root.TryGetProperty("result", out var resEl))
                        {
                            tcs.TrySetResult(resEl);
                        }
                        else
                        {
                            tcs.TrySetResult(root);
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _state = McpConnectionState.Error;
            _lastError = ex.Message;

            foreach (var entry in _pending)
            {
                entry.Value.TrySetException(ex);
            }
            _pending.Clear();
        }
    }
}
