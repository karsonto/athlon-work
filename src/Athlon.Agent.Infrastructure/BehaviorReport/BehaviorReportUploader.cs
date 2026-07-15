using System.Globalization;
using System.Net.Http.Json;
using Athlon.Agent.Core;
using Athlon.Agent.Core.BehaviorReport;

namespace Athlon.Agent.Infrastructure.BehaviorReport;

public sealed class BehaviorReportUploader(
    HttpClient httpClient,
    AppSettings settings,
    BehaviorEventLocalStore store,
    ClientDeviceInfo deviceInfo,
    IAppLogger logger)
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

        try
        {
            using var response = await httpClient.PostAsJsonAsync(endpoint, body, cancellationToken)
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
