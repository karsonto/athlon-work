using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Athlon.Agent.Infrastructure;

public sealed class AuditLogService(IAppLogger logger, IAppPathProvider paths, IJsonFileStore jsonFileStore)
{
    private readonly IAppLogger _logger = logger.ForContext("Audit");

    public async Task WriteAsync(string action, object payload, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(paths.AuditPath, $"audit-{DateTimeOffset.Now:yyyy-MM-dd}.jsonl");
        await jsonFileStore.AppendJsonLineAsync(path, new { time = DateTimeOffset.UtcNow, action, payload }, cancellationToken);
        _logger.Information("Audit entry written for {Action}", action);
    }
}
