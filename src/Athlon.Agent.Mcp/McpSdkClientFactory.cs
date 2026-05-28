using System.Collections;
using Athlon.Agent.Core;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Athlon.Agent.Mcp;

public static class McpSdkClientFactory
{
    public static async Task<IMcpClient> ConnectAsync(
        string name,
        McpServerSettings server,
        string? workspaceRoot,
        string? clientName = null,
        CancellationToken cancellationToken = default)
    {
        var transport = CreateTransport(name, server, workspaceRoot, out var transportLabel, out var getLastStderrLine);
        try
        {
            var protocolClient = await ModelContextProtocol.Client.McpClient.CreateAsync(
                transport,
                new McpClientOptions
                {
                    ClientInfo = new Implementation
                    {
                        Name = clientName ?? "Athlon.Agent",
                        Version = typeof(McpSdkClientFactory).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"
                    },
                    InitializationTimeout = McpClientDefaults.RequestTimeout
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return new SdkMcpClient(name, transportLabel, protocolClient, getLastStderrLine);
        }
        catch
        {
            if (transport is IAsyncDisposable disposableTransport)
            {
                await disposableTransport.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    private static IClientTransport CreateTransport(
        string name,
        McpServerSettings server,
        string? workspaceRoot,
        out string transportLabel,
        out Func<string?>? getLastStderrLine)
    {
        getLastStderrLine = null;

        if (McpTransportKinds.IsStreamableHttp(server.TransportType))
        {
            transportLabel = "streamable-http";
            var httpClient = new HttpClient { Timeout = McpClientDefaults.RequestTimeout };
            return new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Name = name,
                    Endpoint = new Uri(server.Url, UriKind.Absolute),
                    TransportMode = HttpTransportMode.StreamableHttp,
                    AdditionalHeaders = server.Headers.Count == 0
                        ? null
                        : new Dictionary<string, string>(server.Headers)
                },
                httpClient,
                ownsHttpClient: true);
        }

        transportLabel = "stdio";
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string envKey && entry.Value is string envValue)
            {
                environment[envKey] = envValue;
            }
        }

        foreach (var (key, value) in server.Env)
        {
            environment[key] = value;
        }

        if (!string.IsNullOrWhiteSpace(workspaceRoot)
            && !environment.ContainsKey("VISION_WORKSPACE"))
        {
            environment["VISION_WORKSPACE"] = Path.GetFullPath(workspaceRoot);
        }

        string? lastStderr = null;
        getLastStderrLine = () => lastStderr;

        return new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = name,
            Command = server.Command,
            Arguments = server.Args.Count == 0 ? null : server.Args.ToList(),
            WorkingDirectory = ResolveWorkingDirectory(server),
            EnvironmentVariables = environment,
            StandardErrorLines = line => lastStderr = line
        });
    }

    private static string ResolveWorkingDirectory(McpServerSettings server) =>
        !string.IsNullOrWhiteSpace(server.WorkingDirectory)
            ? Path.GetFullPath(server.WorkingDirectory)
            : Environment.CurrentDirectory;
}
