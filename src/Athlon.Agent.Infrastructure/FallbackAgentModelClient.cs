using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure;

public sealed class FallbackAgentModelClient(
    IAgentModelClient primary,
    AppSettings settings,
    IAppLogger logger) : IAgentModelClient
{
    private readonly IAppLogger _logger = logger.ForContext("FallbackModel");

    public async Task<AgentModelResponse> CompleteAsync(
        AgentModelRequest request,
        Func<string, Task>? onTextDelta = null,
        Func<string, Task>? onReasoningDelta = null,
        Func<StreamingToolCallDelta, Task>? onToolCallDelta = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await primary.CompleteAsync(
                request,
                onTextDelta,
                onReasoningDelta,
                onToolCallDelta,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && IsRetryable(ex))
        {
            if (!settings.Model.EnableFallback || string.IsNullOrWhiteSpace(settings.Model.FallbackModelName))
            {
                throw;
            }

            _logger.Warning(
                "Primary model failed with retryable error; switching to fallback model {FallbackModel}: {Message}",
                settings.Model.FallbackModelName,
                ex.Message);

            using (ScheduleTurnScope.Enter(new ScheduleTurnOptions(ModelNameOverride: settings.Model.FallbackModelName)))
            {
                return await primary.CompleteAsync(
                    request,
                    onTextDelta,
                    onReasoningDelta,
                    onToolCallDelta,
                    cancellationToken);
            }
        }
    }

    private static bool IsRetryable(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var message = current.Message;
            if (message.Contains("rate_limit", StringComparison.OrdinalIgnoreCase)
                || message.Contains("overloaded", StringComparison.OrdinalIgnoreCase)
                || message.Contains("too_many_requests", StringComparison.OrdinalIgnoreCase)
                || message.Contains("capacity", StringComparison.OrdinalIgnoreCase)
                || message.Contains("529", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
