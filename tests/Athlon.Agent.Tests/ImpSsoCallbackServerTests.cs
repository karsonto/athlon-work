using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Athlon.Agent.Core.Sso;
using Athlon.Agent.Infrastructure.Sso;

namespace Athlon.Agent.Tests;

[SupportedOSPlatform("windows")]
public sealed class ImpSsoCallbackServerTests
{
    [Fact]
    public async Task CompleteBrowserResponseAsync_ReturnsSuccessJson_WhenValidationSucceeds()
    {
        var settings = CreateSettings();
        using var server = new ImpSsoCallbackServer(settings);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var waitTask = server.WaitForCallbackAsync(TimeSpan.FromSeconds(5), cts.Token);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        var postTask = PostCompleteAsync(http, settings, """{"token":"abc","appId":"252"}""");
        var payload = await waitTask;

        Assert.Equal("abc", payload.Token);

        var session = new ImpSsoSession
        {
            SsoToken = "abc",
            UserId = "u1",
            DisplayName = "User One",
            LoggedInAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(24)
        };
        await server.CompleteBrowserResponseAsync(ImpSsoCheckResult.Success(session), cts.Token);

        var response = await postTask;
        Assert.True(response.Ok);
        Assert.Equal("登录成功", response.Message);

        await server.StopAsync();
    }

    [Fact]
    public async Task CompleteBrowserResponseAsync_ReturnsFailureJson_WhenValidationFails()
    {
        var settings = CreateSettings();
        using var server = new ImpSsoCallbackServer(settings);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var waitTask = server.WaitForCallbackAsync(TimeSpan.FromSeconds(5), cts.Token);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        var postTask = PostCompleteAsync(http, settings, """{"token":"bad","appId":"252"}""");
        await waitTask;

        await server.CompleteBrowserResponseAsync(
            ImpSsoCheckResult.Fail(ImpSsoCheckStatus.Invalid, "IMP 会话已失效。"),
            cts.Token);

        var response = await postTask;
        Assert.False(response.Ok);
        Assert.Equal("IMP 会话已失效。", response.Message);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        await server.StopAsync();
    }

    [Fact]
    public async Task HandleCompleteAsync_ReturnsConflict_WhenDuplicatePostReceived()
    {
        var settings = CreateSettings();
        using var server = new ImpSsoCallbackServer(settings);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var waitTask = server.WaitForCallbackAsync(TimeSpan.FromSeconds(5), cts.Token);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        var firstPostTask = PostCompleteAsync(http, settings, """{"token":"abc","appId":"252"}""");
        await waitTask;

        var duplicateResponse = await PostCompleteAsync(
            http,
            settings,
            """{"token":"xyz","appId":"252"}""");
        Assert.Equal(HttpStatusCode.Conflict, duplicateResponse.StatusCode);
        Assert.False(duplicateResponse.Ok);
        Assert.Equal("登录已在处理中", duplicateResponse.Message);

        await server.CompleteBrowserResponseAsync(
            ImpSsoCheckResult.Success(new ImpSsoSession
            {
                SsoToken = "abc",
                UserId = "u1",
                DisplayName = "User One",
                LoggedInAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(24)
            }),
            cts.Token);

        var firstResponse = await firstPostTask;
        Assert.True(firstResponse.Ok);

        await server.StopAsync();
    }

    [Fact]
    public async Task StopAsync_WaitsForPendingResponse_BeforeClosingListener()
    {
        var settings = CreateSettings();
        using var server = new ImpSsoCallbackServer(settings);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var waitTask = server.WaitForCallbackAsync(TimeSpan.FromSeconds(5), cts.Token);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        var postTask = PostCompleteAsync(http, settings, """{"token":"abc","appId":"252"}""");
        await waitTask;

        var stopTask = server.StopAsync();
        await Task.Delay(100);

        await server.CompleteBrowserResponseAsync(
            ImpSsoCheckResult.Success(new ImpSsoSession
            {
                SsoToken = "abc",
                UserId = "u1",
                DisplayName = "User One",
                LoggedInAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(24)
            }),
            cts.Token);

        var response = await postTask;
        Assert.True(response.Ok);

        await stopTask;
    }

    [Fact]
    public async Task AbortPendingBrowserResponseAsync_ClosesPendingConnection()
    {
        var settings = CreateSettings();
        using var server = new ImpSsoCallbackServer(settings);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var waitTask = server.WaitForCallbackAsync(TimeSpan.FromSeconds(5), cts.Token);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        var postTask = PostCompleteAsync(http, settings, """{"token":"abc","appId":"252"}""");
        await waitTask;

        await server.AbortPendingBrowserResponseAsync();

        CompleteResponse? response = null;
        Exception? error = null;
        try
        {
            response = await postTask;
        }
        catch (Exception ex)
        {
            error = ex;
        }

        Assert.True(
            error is not null || response?.StatusCode == HttpStatusCode.ServiceUnavailable,
            "Abort should close the pending connection or return 503.");

        await server.StopAsync();
    }

    private static SsoSettings CreateSettings()
    {
        return new SsoSettings
        {
            CallbackPort = GetEphemeralPort()
        };
    }

    private static int GetEphemeralPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<CompleteResponse> PostCompleteAsync(
        HttpClient http,
        SsoSettings settings,
        string jsonBody)
    {
        using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(
            $"{settings.CallbackBaseUrl}{settings.CompletePath}",
            content);

        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var ok = root.TryGetProperty("ok", out var okElement) && okElement.GetBoolean();
        var message = root.TryGetProperty("message", out var messageElement)
            ? messageElement.GetString() ?? ""
            : "";

        return new CompleteResponse(response.StatusCode, ok, message);
    }

    private sealed record CompleteResponse(HttpStatusCode StatusCode, bool Ok, string Message);
}
