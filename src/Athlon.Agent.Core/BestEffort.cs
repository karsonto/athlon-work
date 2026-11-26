namespace Athlon.Agent.Core;

/// <summary>
/// Small helpers for teardown and optional side effects where failure must not
/// escape or interrupt the caller. Prefer these over empty <c>catch { }</c> so
/// the intent is explicit and logging stays consistent.
/// </summary>
public static class BestEffort
{
    public static void Ignore(Action action, IAppLogger? logger = null, string? reason = null)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            LogIgnored(ex, logger, reason);
        }
    }

    public static async Task IgnoreAsync(Func<Task> action, IAppLogger? logger = null, string? reason = null)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogIgnored(ex, logger, reason);
        }
    }

    public static void LogAndContinue(
        Exception exception,
        IAppLogger logger,
        string messageTemplate,
        params object[] values)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(logger);
        logger.Error(exception, messageTemplate, values);
    }

    private static void LogIgnored(Exception exception, IAppLogger? logger, string? reason)
    {
        if (logger is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            logger.Debug("Best-effort action ignored: {Error}", exception.Message);
            return;
        }

        logger.Debug("Best-effort {Reason} ignored: {Error}", reason, exception.Message);
    }
}
