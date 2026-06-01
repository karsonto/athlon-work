using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

public sealed class TurnFailureMessagesTests
{
    [Fact]
    public void FormatModelCallFailure_IncludesPath_ForAccessDenied()
    {
        var ex = new UnauthorizedAccessException("Access to the path 'C:\\data\\session.json' is denied.");
        var message = TurnFailureMessages.FormatModelCallFailure(ex);

        Assert.StartsWith(TurnFailureMessages.ModelCallFailedPrefix, message, StringComparison.Ordinal);
        Assert.Contains("session.json", message, StringComparison.Ordinal);
        Assert.DoesNotContain("模型调用失败：模型调用失败", message, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatModelCallFailure_DoesNotDoublePrefix()
    {
        var ex = new InvalidOperationException("模型调用失败：timeout");
        var message = TurnFailureMessages.FormatModelCallFailure(ex);

        Assert.Equal("模型调用失败：timeout", message);
    }
}
