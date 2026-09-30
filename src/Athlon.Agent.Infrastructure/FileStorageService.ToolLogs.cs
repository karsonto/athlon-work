using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.RuntimeDiagnostics;
using Athlon.Agent.Infrastructure.BehaviorReport;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Athlon.Agent.Infrastructure;

/// <summary>Tool-call log and attempt-event persistence.</summary>
public sealed partial class FileStorageService
{
    public Task AppendToolCallLogAsync(string sessionId, SessionToolCallLogEntry entry, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task AppendAttemptEventAsync(
        string sessionId,
        AgentAttemptEvent entry,
        CancellationToken cancellationToken = default)
    {
        try
        {
            BehaviorEventManager.Instance.RecordAttempt(entry);
        }
        catch
        {
            // Behavior reporting must never affect persistence.
        }

        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<AgentAttemptEvent>> LoadAttemptEventsAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        using (await SessionWriteLock.AcquireAsync(sessionId, cancellationToken).ConfigureAwait(false))
        {
            var path = Path.Combine(GetSessionDirectory(sessionId), "attempts.jsonl");
            if (!File.Exists(path))
            {
                return Array.Empty<AgentAttemptEvent>();
            }

            var events = new List<AgentAttemptEvent>();
            foreach (var line in await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false))
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    var item = JsonSerializer.Deserialize<AgentAttemptEvent>(line, JsonFileStore.JsonLineOptions);
                    if (item is not null)
                    {
                        events.Add(item);
                    }
                }
            }
            return events;
        }
    }

    public Task FlushPendingToolCallLogsAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    private Task WriteToolCallLogCoreAsync(
        string sessionId,
        SessionToolCallLogEntry entry,
        CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
