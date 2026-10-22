using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure;

public sealed class AuditLogService(IAppPathProvider paths, IJsonFileStore jsonFileStore)
{
    public async Task WriteAsync(string action, object payload, CancellationToken cancellationToken = default)
    {
        var now = AppTimeZone.Now;
        var path = Path.Combine(paths.AuditPath, $"audit-{now:yyyy-MM-dd}.jsonl");
        await jsonFileStore.AppendJsonLineAsync(path, new { time = now, action, payload }, cancellationToken);
    }
}
