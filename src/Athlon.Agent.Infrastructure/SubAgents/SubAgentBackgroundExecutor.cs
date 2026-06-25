using System.Collections.Concurrent;
using Athlon.Agent.Core;
using Athlon.Agent.Core.SubAgents;

namespace Athlon.Agent.Infrastructure.SubAgents;

public sealed class SubAgentBackgroundExecutor(
    AppSettings settings,
    SubAgentRunExecutor runExecutor,
    ISubAgentRegistry registry,
    ISubAgentTaskStore taskStore,
    ISubAgentCompletionStore completionStore,
    IAppLogger logger)
{
    private readonly SubAgentSettings _subAgent = settings.SubAgent;
    private readonly IAppLogger _logger = logger.ForContext("SubAgentBackgroundExecutor");
    private readonly ConcurrentQueue<SubAgentBackgroundWorkItem> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private SemaphoreSlim? _concurrency;
    private CancellationTokenSource? _cts;
    private Task? _worker;

    public void Start()
    {
        if (_worker is not null)
        {
            return;
        }

        _concurrency = new SemaphoreSlim(_subAgent.MaxConcurrentSubAgents, _subAgent.MaxConcurrentSubAgents);
        _cts = new CancellationTokenSource();
        _worker = Task.Run(() => WorkerLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        if (_cts is null)
        {
            return;
        }

        _cts.Cancel();
        try
        {
            _worker?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        _cts.Dispose();
        _cts = null;
        _worker = null;
    }

    public void Enqueue(SubAgentBackgroundWorkItem item)
    {
        _queue.Enqueue(item);
        _signal.Release();
    }

    private async Task WorkerLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (!_queue.TryDequeue(out var item))
            {
                continue;
            }

            await (_concurrency ?? new SemaphoreSlim(1)).WaitAsync(cancellationToken).ConfigureAwait(false);
            _ = Task.Run(async () =>
            {
                try
                {
                    await ProcessItemAsync(item, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _concurrency?.Release();
                }
            }, cancellationToken);
        }
    }

    private async Task ProcessItemAsync(SubAgentBackgroundWorkItem item, CancellationToken cancellationToken)
    {
        var record = await taskStore.GetAsync(item.ParentSessionId, item.TaskId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return;
        }

        record = record with { Status = "running" };
        await taskStore.UpdateAsync(item.ParentSessionId, record, cancellationToken).ConfigureAwait(false);

        var outcome = await runExecutor.ExecuteAsync(
            item.ParentSessionId,
            item.SubSessionId,
            item.Role,
            item.Message,
            cancellationToken).ConfigureAwait(false);

        await registry.UpdateLastActivityAsync(item.ParentSessionId, item.SubSessionId, cancellationToken).ConfigureAwait(false);

        var entry = await registry.FindBySubSessionIdAsync(item.ParentSessionId, item.SubSessionId, cancellationToken).ConfigureAwait(false);
        if (entry is not null)
        {
            var status = outcome.IsSuccess ? "ok" : "error";
            var completedAt = DateTimeOffset.UtcNow;
            var announce = SubAgentResultFormatter.FormatAnnounceText(
                entry,
                item.RunId,
                status,
                outcome.ResultText,
                outcome.Error,
                completedAt);
            await completionStore.AppendAsync(
                item.ParentSessionId,
                new PendingCompletion(
                    item.RunId,
                    item.SessionKey,
                    item.ParentSessionId,
                    status,
                    outcome.ResultText,
                    outcome.Error,
                    completedAt,
                    announce),
                cancellationToken).ConfigureAwait(false);
        }

        record = record with
        {
            Status = outcome.IsSuccess ? "completed" : "failed",
            Result = outcome.ResultText,
            Error = outcome.Error,
            CompletedAt = DateTimeOffset.UtcNow
        };
        await taskStore.UpdateAsync(item.ParentSessionId, record, cancellationToken).ConfigureAwait(false);

        if (!outcome.IsSuccess)
        {
            _logger.Warning("Background sub-agent failed task={TaskId} error={Error}", item.TaskId, outcome.Error);
        }
    }
}
