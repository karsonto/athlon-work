using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Athlon.Agent.Core;
using Athlon.Agent.Core.BehaviorReport;

namespace Athlon.Agent.Infrastructure.BehaviorReport;

public sealed class BehaviorReportUploader(
    HttpClient httpClient,
    AppSettings settings,
    BehaviorEventLocalStore store,
    ClientDeviceInfo deviceInfo,
    IAppLogger logger,
    ICredentialStore? credentialStore = null)
{
    private readonly IAppLogger _logger = logger.ForContext("BehaviorReportUploader");

    public async Task<int> UploadPendingAsync(CancellationToken cancellationToken = default)
    {
        var report = settings.BehaviorReport;
        if (!report.Enabled || string.IsNullOrWhiteSpace(report.BaseUrl))
        {
            return 0;
        }

        var pending = await store.ReadAllAsync(cancellationToken).ConfigureAwait(false);
        if (pending.Count == 0)
        {
            return 0;
        }

        var device = deviceInfo.GetSnapshot();
        var endpoint = report.BaseUrl.TrimEnd('/') + "/agent/report";
        var body = BuildRequestBody(device, pending);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(body)
        };

        // Identity: the model API key doubles as the dashboard credential (the server matches
        // SHA-256(key) against api_keys.api_key_hash). Reporting stays best-effort, so a missing key
        // downgrades the report to anonymous rather than dropping it.
        var apiKey = await ResolveApiKeyAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.Warning(
                    "Behavior report batch upload failed ({Count} events): HTTP {StatusCode}",
                    pending.Count,
                    (int)response.StatusCode);
                return 0;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warning(
                "Behavior report batch upload error ({Count} events): {Error}",
                pending.Count,
                ex.Message);
            return 0;
        }

        var uploadedIds = pending.Select(e => e.Id).ToList();
        await store.RemoveUploadedAsync(uploadedIds, cancellationToken).ConfigureAwait(false);
        return uploadedIds.Count;
    }

    private async Task<string?> ResolveApiKeyAsync(CancellationToken cancellationToken)
    {
        if (credentialStore is null)
        {
            return null;
        }

        try
        {
            return await ModelApiKeyResolver.ResolveAsync(credentialStore, settings, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warning("Behavior report api key resolution failed: {Error}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Builds POST /agent/report body: device fields + batched <c>events</c>.
    /// </summary>
    internal static object BuildRequestBody(ClientDeviceSnapshot device, IReadOnlyList<BehaviorEvent> events) =>
        new
        {
            user_id = device.UserId,
            client_ip = device.ClientIp,
            mac_address = device.MacAddress,
            os_version = device.OsVersion,
            app_name = device.AppName,
            app_version = device.AppVersion,
            app_file_version = device.AppFileVersion,
            screen_resolution = device.ScreenResolution,
            events = events.Select(BuildEventItem).ToArray()
        };

    internal static object BuildEventItem(BehaviorEvent evt)
    {
        // Server event_type is the business event id (e.g. user_login / mcp_tool).
        // Internal action/event category is kept in event_params as "event_kind".
        var parameters = new Dictionary<string, object?>(evt.Parameters, StringComparer.Ordinal);
        if (!parameters.ContainsKey("event_kind") && !string.IsNullOrWhiteSpace(evt.EventType))
        {
            parameters["event_kind"] = evt.EventType;
        }

        return new
        {
            event_type = string.IsNullOrWhiteSpace(evt.EventId) ? evt.EventType : evt.EventId,
            event_params = parameters,
            message_content = string.IsNullOrWhiteSpace(evt.MessageContent) ? evt.EventId : evt.MessageContent,
            event_time = FormatEventTime(evt.Timestamp)
        };
    }

    /// <summary>Formats as China Standard Time (UTC+8): yyyy-MM-dd HH:mm:ss.fff</summary>
    internal static string FormatEventTime(DateTimeOffset timestamp) =>
        AppTimeZone.ToChina(timestamp).ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
}
