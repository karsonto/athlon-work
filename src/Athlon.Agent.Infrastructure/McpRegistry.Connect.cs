using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Athlon.Agent.Core;
using Athlon.Agent.Core.BehaviorReport;
using Athlon.Agent.Core.RuntimeDiagnostics;
using Athlon.Agent.Infrastructure.BehaviorReport;
using Athlon.Agent.Infrastructure.Sso;
using Athlon.Agent.Mcp;

namespace Athlon.Agent.Infrastructure;

/// <summary>Server refresh, connect and reconnect.</summary>
public sealed partial class McpRegistry
{
    public async Task RefreshAsync(
        IReadOnlyList<McpServerSettings> settings,
        CancellationToken cancellationToken = default,
        Action? onStatusesChanged = null)
    {
        var toConnect = new List<(string Name, McpServerSettings Server, string Fingerprint, long Generation)>();
        var removedAny = false;
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            var enabled = settings
                .Where(server => server.Enabled
                    && !string.IsNullOrWhiteSpace(server.Name)
                    && IsValidServerConfig(server)
                    && IsSupportedTransport(server.TransportType))
                .ToDictionary(server => server.Name.Trim(), StringComparer.OrdinalIgnoreCase);

            // Stop disabled/removed servers.
            foreach (var existing in _clients.Keys.ToArray())
            {
                if (!enabled.ContainsKey(existing) && _clients.TryRemove(existing, out var removed))
                {
                    InvalidateConnect(existing);
                    _tools.TryRemove(existing, out _);
                    _configFingerprints.TryRemove(existing, out _);
                    _toolCallTimeoutSeconds.TryRemove(existing, out _);
                    _statuses[existing] = new McpServerStatus(existing, McpConnectionState.Disabled, "stdio", Array.Empty<McpTool>());
                    try { await removed.DisposeAsync(); } catch { /* ignore */ }
                    RecordMcpServer(existing, "disconnected");
                    removedAny = true;
                }
            }

            if (removedAny)
            {
                onStatusesChanged?.Invoke();
            }

            foreach (var (name, server) in enabled)
            {
                var fingerprint = CreateEffectiveFingerprint(server, workspaceContext.RootPath);
                _toolCallTimeoutSeconds[name] = Math.Clamp(server.ToolCallTimeoutSeconds, 1, 3600);
                if (_clients.ContainsKey(name)
                    && _configFingerprints.TryGetValue(name, out var existingFingerprint)
                    && string.Equals(existingFingerprint, fingerprint, StringComparison.Ordinal))
                {
                    continue;
                }

                if (_clients.TryRemove(name, out var existing))
                {
                    try { await existing.DisposeAsync(); } catch { /* ignore */ }
                }

                var transportLabel = ResolveTransportLabel(server);
                _tools[name] = Array.Empty<McpTool>();
                _statuses[name] = new McpServerStatus(
                    name,
                    McpConnectionState.Connecting,
                    transportLabel,
                    Array.Empty<McpTool>());
                var generation = InvalidateConnect(name);
                toConnect.Add((name, server, fingerprint, generation));
            }

            if (toConnect.Count > 0)
            {
                onStatusesChanged?.Invoke();
            }
        }
        finally
        {
            if (removedAny)
            {
                InvalidateCatalogCache();
            }

            _refreshLock.Release();
        }

        if (toConnect.Count == 0)
        {
            return;
        }

        // Connect outside the refresh lock so one slow server cannot block other MCP toggles.
        var connectTasks = toConnect.Select(item => ConnectOneSafeAsync(
            item.Name,
            item.Server,
            item.Fingerprint,
            item.Generation,
            onStatusesChanged,
            cancellationToken));
        await Task.WhenAll(connectTasks).ConfigureAwait(false);
        InvalidateCatalogCache();
    }

    private async Task ConnectOneSafeAsync(
        string name,
        McpServerSettings server,
        string fingerprint,
        long generation,
        Action? onStatusesChanged,
        CancellationToken cancellationToken)
    {
        try
        {
            await ConnectOneAsync(name, server, fingerprint, generation, onStatusesChanged, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer refresh; safe to ignore.
        }
    }

    public async Task ReconnectAsync(
        string serverName,
        IReadOnlyList<McpServerSettings> settings,
        CancellationToken cancellationToken = default,
        Action? onStatusesChanged = null)
    {
        if (string.IsNullOrWhiteSpace(serverName))
        {
            return;
        }

        string? name = null;
        McpServerSettings? server = null;
        string? fingerprint = null;
        long generation = 0;
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            server = settings.FirstOrDefault(item =>
                item.Enabled
                && !string.IsNullOrWhiteSpace(item.Name)
                && string.Equals(item.Name.Trim(), serverName.Trim(), StringComparison.OrdinalIgnoreCase)
                && IsValidServerConfig(item)
                && IsSupportedTransport(item.TransportType));
            if (server is null)
            {
                return;
            }

            name = server.Name.Trim();
            if (_clients.TryRemove(name, out var existing))
            {
                try { await existing.DisposeAsync(); } catch { /* ignore */ }
            }

            _configFingerprints.TryRemove(name, out _);
            _tools[name] = Array.Empty<McpTool>();
            fingerprint = CreateEffectiveFingerprint(server, workspaceContext.RootPath);
            _toolCallTimeoutSeconds[name] = Math.Clamp(server.ToolCallTimeoutSeconds, 1, 3600);
            var transportLabel = ResolveTransportLabel(server);
            _statuses[name] = new McpServerStatus(
                name,
                McpConnectionState.Connecting,
                transportLabel,
                Array.Empty<McpTool>());
            generation = InvalidateConnect(name);
            onStatusesChanged?.Invoke();
        }
        finally
        {
            _refreshLock.Release();
        }

        if (name is null || server is null || fingerprint is null)
        {
            return;
        }

        await ConnectOneAsync(name, server, fingerprint, generation, onStatusesChanged, cancellationToken)
            .ConfigureAwait(false);
        InvalidateCatalogCache();
    }

    private long InvalidateConnect(string name) =>
        _connectGenerations.AddOrUpdate(name, 1, static (_, generation) => generation + 1);

    private bool IsConnectSuperseded(string name, long generation) =>
        !_connectGenerations.TryGetValue(name, out var current) || current != generation;

    private async Task ConnectOneAsync(
        string name,
        McpServerSettings server,
        string fingerprint,
        long generation,
        Action? onStatusesChanged,
        CancellationToken cancellationToken)
    {
        var transportLabel = ResolveTransportLabel(server);
        IMcpClient client;
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(McpClientDefaults.ConnectInitializationTimeout + TimeSpan.FromSeconds(5));

            client = await McpSdkClientFactory.ConnectAsync(
                name,
                WithStdioSsoEnvironment(server),
                workspaceContext.RootPath,
                clientName: "Athlon.Agent",
                connectCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (IsConnectSuperseded(name, generation))
            {
                return;
            }

            var message = ex is OperationCanceledException
                ? $"Timed out connecting to MCP server ({transportLabel})."
                : ex.Message;
            _logger.Warning("MCP connect failed for {Server}: {Message}", name, message);

            var context = _runContextAccessor.Current;
            var evt = new RuntimeDiagnosticEvent(
                eventId: "",
                ts: default,
                sequence: 0,
                sessionId: context?.SessionId,
                runId: context?.RunId,
                turnId: null,
                attemptId: null,
                parentAttemptId: null,
                toolCallId: null,
                messageId: null,
                component: RuntimeDiagnosticComponent.Mcp,
                phase: RuntimeDiagnosticPhase.Request,
                eventType: "mcp.connect_failed",
                severity: RuntimeDiagnosticSeverity.Error,
                errorCode: RuntimeDiagnosticErrorCodes.McpConnectFailed,
                message: message);
            await _runtimeDiagnosticEventSink.EnqueueAsync(evt, CancellationToken.None).ConfigureAwait(false);

            _tools[name] = Array.Empty<McpTool>();
            _statuses[name] = new McpServerStatus(
                name,
                McpConnectionState.Error,
                transportLabel,
                Array.Empty<McpTool>(),
                LastError: message);
            RecordMcpServer(name, "disconnected", errorType: ex.GetType().Name);
            onStatusesChanged?.Invoke();
            return;
        }

        if (IsConnectSuperseded(name, generation))
        {
            try { await client.DisposeAsync(); } catch { /* ignore */ }
            return;
        }

        _clients[name] = client;
        _configFingerprints[name] = fingerprint;
        try
        {
            var tools = await client.ListToolsAsync(cancellationToken).ConfigureAwait(false);
            if (IsConnectSuperseded(name, generation))
            {
                _clients.TryRemove(name, out _);
                _configFingerprints.TryRemove(name, out _);
                try { await client.DisposeAsync(); } catch { /* ignore */ }
                return;
            }

            _tools[name] = tools;
            _statuses[name] = client.Status with { Tools = tools.ToArray() };
            RecordMcpServer(name, "connected", toolCount: tools.Count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _clients.TryRemove(name, out _);
            _configFingerprints.TryRemove(name, out _);
            try { await client.DisposeAsync(); } catch { /* ignore */ }
            throw;
        }
        catch (OperationCanceledException)
        {
            // Another refresh disposed this client while list-tools was in flight.
            _clients.TryRemove(name, out _);
            _configFingerprints.TryRemove(name, out _);
            try { await client.DisposeAsync(); } catch { /* ignore */ }
            return;
        }
        catch (Exception ex)
        {
            if (IsConnectSuperseded(name, generation))
            {
                _clients.TryRemove(name, out _);
                _configFingerprints.TryRemove(name, out _);
                try { await client.DisposeAsync(); } catch { /* ignore */ }
                return;
            }

            _logger.Warning("MCP refresh failed for {Server}: {Message}", name, ex.Message);

            var context = _runContextAccessor.Current;
            var evt = new RuntimeDiagnosticEvent(
                eventId: "",
                ts: default,
                sequence: 0,
                sessionId: context?.SessionId,
                runId: context?.RunId,
                turnId: null,
                attemptId: null,
                parentAttemptId: null,
                toolCallId: null,
                messageId: null,
                component: RuntimeDiagnosticComponent.Mcp,
                phase: RuntimeDiagnosticPhase.Request,
                eventType: "mcp.list_tools_failed",
                severity: RuntimeDiagnosticSeverity.Error,
                errorCode: RuntimeDiagnosticErrorCodes.McpListToolsFailed,
                message: ex.Message);
            await _runtimeDiagnosticEventSink.EnqueueAsync(evt, CancellationToken.None).ConfigureAwait(false);

            _tools[name] = Array.Empty<McpTool>();
            _statuses[name] = client.Status with
            {
                State = McpConnectionState.Error,
                Tools = Array.Empty<McpTool>(),
                LastError = ex.Message
            };
            RecordMcpServer(name, "disconnected", errorType: ex.GetType().Name);
        }

        onStatusesChanged?.Invoke();
    }

    private static void RecordMcpServer(string serverName, string action, int? toolCount = null, string? errorType = null)
    {
        try
        {
            BehaviorEventManager.Instance.Record(
                BehaviorEventIds.McpServer,
                BehaviorEventTypes.Event,
                BehaviorEventIds.McpServer,
                new Dictionary<string, object?>
                {
                    ["server_name"] = serverName,
                    ["action"] = action,
                    ["tool_count"] = toolCount,
                    ["error_type"] = errorType
                });
        }
        catch
        {
            // ignore
        }
    }
}
