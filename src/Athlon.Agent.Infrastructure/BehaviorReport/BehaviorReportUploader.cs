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
        var uploaded = new List<string>();
        var endpoint = report.BaseUrl.TrimEnd('/') + "/agent/report";

        foreach (var evt in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var body = BuildRequestBody(device, evt);
                using var response = await httpClient.PostAsJsonAsync(endpoint, body, cancellationToken)
                    .ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    uploaded.Add(evt.Id);
                }
                else
                {
                    _logger.Warning(
                        "Behavior report upload failed for {EventId}: HTTP {StatusCode}",
                        evt.EventId,
                        (int)response.StatusCode);
                    // Keep failed events; retry next cycle. Stop batch so order is preserved.
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Warning("Behavior report upload error for {EventId}: {Error}", evt.EventId, ex.Message);
                break;
            }
        }

        if (uploaded.Count > 0)
        {
            await store.RemoveUploadedAsync(uploaded, cancellationToken).ConfigureAwait(false);
        }

        return uploaded.Count;
    }

    internal static object BuildRequestBody(ClientDeviceSnapshot device, BehaviorEvent evt) =>
        new
        {
            user_id = device.UserId,
            client_ip = device.ClientIp,
            mac_address = device.MacAddress,
            os_version = device.OsVersion,
            app_name = device.AppName,
            app_version = device.AppVersion,
            screen_resolution = device.ScreenResolution,
            event_type = evt.EventType,
            event_params = evt.Parameters.Count == 0
                ? new Dictionary<string, object?>(StringComparer.Ordinal)
                : evt.Parameters,
            message_content = string.IsNullOrWhiteSpace(evt.MessageContent) ? evt.EventId : evt.MessageContent
        };
}
