using System.Collections.Concurrent;
using Athlon.Agent.Core;
using Athlon.Agent.Core.SubAgents;

namespace Athlon.Agent.Infrastructure.SubAgents;

public sealed class SubAgentSessionManager(
    AppSettings settings,
    ISubAgentRegistry registry,
    ISubAgentSessionStore sessionStore,
    ISubAgentTaskStore taskStore,
    ISubAgentCompletionStore completionStore,
    SubAgentRunExecutor runExecutor,
    SubAgentBackgroundExecutor backgroundExecutor,
    IAppLogger logger) : ISubAgentSessionManager
{
    private readonly SubAgentSettings _subAgent = settings.SubAgent;
    private readonly IAppLogger _logger = logger.ForContext("SubAgentSessionManager");
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionLocks = new(StringComparer.Ordinal);

    public async Task<SpawnResult> SpawnAsync(
        string parentSessionId,
        string role,
        string? message,
        string? label,
        int? timeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(role))
        {
            return new SpawnResult("error", null, string.Empty, string.Empty, string.Empty, "role is required", null, false, null);
        }

        var timeout = NormalizeTimeout(timeoutSeconds);
        var reused = false;
        SubAgentSessionEntry? existing = null;

        if (!string.IsNullOrWhiteSpace(label))
        {
            existing = await registry.FindByLabelAsync(parentSessionId, label, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                reused = true;
            }
        }

        string subSessionId;
        string sessionKey;
        string sessionFilePath;
        string spawnRunId;

        if (existing is not null)
        {
            subSessionId = existing.SubSessionId;
            sessionKey = existing.SessionKey;
            sessionFilePath = existing.SessionFilePath;
            spawnRunId = existing.SpawnRunId;
            if (!string.Equals(existing.Role, role.Trim(), StringComparison.Ordinal))
            {
                var bundle = await sessionStore.LoadAsync(parentSessionId, subSessionId, cancellationToken).ConfigureAwait(false);
                if (bundle is not null)
                {
                    await sessionStore.SaveAsync(
                        parentSessionId,
                        subSessionId,
                        bundle with { Role = role.Trim() },
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }
        else
        {
            subSessionId = Guid.NewGuid().ToString("N");
            sessionKey = SubAgentSessionKey.Build(parentSessionId, subSessionId);
            sessionFilePath = registry.GetSessionFilePath(parentSessionId, subSessionId);
            spawnRunId = $"run_{Guid.NewGuid():N}";
            var now = DateTimeOffset.UtcNow;
            await registry.RegisterAsync(
                parentSessionId,
                subSessionId,
                new SubAgentMetaFile
                {
                    Role = role.Trim(),
                    Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim(),
                    SpawnRunId = spawnRunId,
                    CreatedAt = now,
                    LastActivityAt = now
                },
                cancellationToken).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            return new SpawnResult(
                "ok",
                spawnRunId,
                sessionKey,
                subSessionId,
                sessionFilePath,
                null,
                null,
                reused,
                null);
        }

        if (timeout == 0)
        {
            var task = await taskStore.CreateAsync(parentSessionId, sessionKey, subSessionId, cancellationToken).ConfigureAwait(false);
            backgroundExecutor.Enqueue(new SubAgentBackgroundWorkItem(
                parentSessionId,
                subSessionId,
                sessionKey,
                role.Trim(),
                message.Trim(),
                task.TaskId,
                spawnRunId));
            return new SpawnResult(
                "accepted",
                spawnRunId,
                sessionKey,
                subSessionId,
                sessionFilePath,
                null,
                task.TaskId,
                reused,
                null);
        }

        var send = await SendAsync(parentSessionId, sessionKey, null, message, timeout, cancellationToken).ConfigureAwait(false);
        if (!send.IsOk)
        {
            return new SpawnResult(
                send.Status,
                spawnRunId,
                sessionKey,
                subSessionId,
                sessionFilePath,
                send.Error,
                send.TaskId,
                reused,
                null);
        }

        return new SpawnResult(
            "ok",
            spawnRunId,
            sessionKey,
            subSessionId,
            sessionFilePath,
            null,
            null,
            reused,
            send.Reply);
    }

    public async Task<SendResult> SendAsync(
        string parentSessionId,
        string? sessionKey,
        string? label,
        string message,
        int? timeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return new SendResult("error", sessionKey ?? string.Empty, null, "message is required", null);
        }

        var entry = await ResolveEntryAsync(parentSessionId, sessionKey, label, cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            return new SendResult("error", sessionKey ?? string.Empty, null, "Unknown sub-agent session.", null);
        }

        var timeout = NormalizeTimeout(timeoutSeconds);
        if (timeout == 0)
        {
            var task = await taskStore.CreateAsync(parentSessionId, entry.SessionKey, entry.SubSessionId, cancellationToken).ConfigureAwait(false);
            backgroundExecutor.Enqueue(new SubAgentBackgroundWorkItem(
                parentSessionId,
                entry.SubSessionId,
                entry.SessionKey,
                entry.Role,
                message.Trim(),
                task.TaskId,
                entry.SpawnRunId));
            return new SendResult("accepted", entry.SessionKey, null, null, task.TaskId);
        }

        var gate = _sessionLocks.GetOrAdd(entry.SessionKey, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var timeoutCts = timeout > 0
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : null;
            if (timeout > 0)
            {
                timeoutCts!.CancelAfter(TimeSpan.FromSeconds(timeout));
            }

            var token = timeoutCts?.Token ?? cancellationToken;
            var outcome = await runExecutor.ExecuteAsync(
                parentSessionId,
                entry.SubSessionId,
                entry.Role,
                message.Trim(),
                token).ConfigureAwait(false);

            await registry.UpdateLastActivityAsync(parentSessionId, entry.SubSessionId, cancellationToken).ConfigureAwait(false);

            if (!outcome.IsSuccess)
            {
                await EnqueueCompletionAsync(
                    parentSessionId,
                    entry,
                    entry.SpawnRunId,
                    "error",
                    null,
                    outcome.Error,
                    cancellationToken).ConfigureAwait(false);
                return new SendResult("error", entry.SessionKey, null, outcome.Error, null);
            }

            var formatted = SubAgentResultFormatter.FormatTrustedReply(
                entry.SessionKey,
                entry.SubSessionId,
                entry.SessionFilePath,
                outcome.ResultText ?? string.Empty);
            await EnqueueCompletionAsync(
                parentSessionId,
                entry,
                entry.SpawnRunId,
                "ok",
                outcome.ResultText,
                null,
                cancellationToken).ConfigureAwait(false);
            return new SendResult("ok", entry.SessionKey, formatted, null, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new SendResult("error", entry.SessionKey, null, $"Timed out after {timeout} seconds.", null);
        }
        finally
        {
            gate.Release();
        }
    }

    public Task<IReadOnlyList<SubAgentSessionEntry>> ListAsync(
        string parentSessionId,
        CancellationToken cancellationToken = default) =>
        registry.ListAsync(parentSessionId, cancellationToken);

    public async Task<HistoryResult> HistoryAsync(
        string parentSessionId,
        string sessionKey,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var entry = await registry.FindBySessionKeyAsync(sessionKey, cancellationToken).ConfigureAwait(false);
        if (entry is null || !string.Equals(entry.ParentSessionId, parentSessionId, StringComparison.Ordinal))
        {
            return new HistoryResult(sessionKey, null, null, "Unknown session_key.");
        }

        var bundle = await sessionStore.LoadAsync(parentSessionId, entry.SubSessionId, cancellationToken).ConfigureAwait(false);
        if (bundle is null)
        {
            return new HistoryResult(sessionKey, entry.SessionFilePath, null, "Session file missing.");
        }

        var take = Math.Max(1, limit);
        var lines = new List<string>();
        foreach (var message in bundle.Session.Messages.TakeLast(take))
        {
            lines.Add($"[{message.Role}] {message.Content}");
        }

        return new HistoryResult(sessionKey, entry.SessionFilePath, string.Join(Environment.NewLine, lines), null);
    }

    public Task<IReadOnlyList<PendingCompletion>> DrainCompletionsAsync(
        string parentSessionId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var take = Math.Clamp(limit, 1, _subAgent.MaxPendingCompletionsPerParent);
        return completionStore.DrainAsync(parentSessionId, take, cancellationToken);
    }

    public Task<SubAgentTaskRecord?> GetTaskOutputAsync(
        string parentSessionId,
        string taskId,
        CancellationToken cancellationToken = default) =>
        taskStore.GetAsync(parentSessionId, taskId, cancellationToken);

    private async Task<SubAgentSessionEntry?> ResolveEntryAsync(
        string parentSessionId,
        string? sessionKey,
        string? label,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(sessionKey))
        {
            var entry = await registry.FindBySessionKeyAsync(sessionKey.Trim(), cancellationToken).ConfigureAwait(false);
            if (entry is not null && string.Equals(entry.ParentSessionId, parentSessionId, StringComparison.Ordinal))
            {
                return entry;
            }
        }

        if (!string.IsNullOrWhiteSpace(label))
        {
            return await registry.FindByLabelAsync(parentSessionId, label, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private async Task EnqueueCompletionAsync(
        string parentSessionId,
        SubAgentSessionEntry entry,
        string runId,
        string status,
        string? resultText,
        string? error,
        CancellationToken cancellationToken)
    {
        var completedAt = DateTimeOffset.UtcNow;
        var announce = SubAgentResultFormatter.FormatAnnounceText(entry, runId, status, resultText, error, completedAt);
        await completionStore.AppendAsync(
            parentSessionId,
            new PendingCompletion(
                runId,
                entry.SessionKey,
                parentSessionId,
                status,
                resultText,
                error,
                completedAt,
                announce),
            cancellationToken).ConfigureAwait(false);
    }

    private int NormalizeTimeout(int? timeoutSeconds)
    {
        if (!timeoutSeconds.HasValue)
        {
            return _subAgent.DefaultSyncTimeoutSeconds;
        }

        if (timeoutSeconds.Value <= 0)
        {
            return 0;
        }

        return Math.Clamp(timeoutSeconds.Value, 1, _subAgent.MaxSyncTimeoutSeconds);
    }
}
