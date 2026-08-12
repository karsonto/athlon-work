using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure.Terminal;

internal static class TerminalToolHelper
{
    public static async Task<ToolResult> InvokeHostAsync(
        Func<CancellationToken, Task<ToolResult>> action,
        CancellationToken cancellationToken)
    {
        try
        {
            return await action(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolResult.Failure("Terminal automation failed", ex.Message);
        }
    }
}
