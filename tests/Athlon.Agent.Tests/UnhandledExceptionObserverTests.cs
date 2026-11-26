using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

public sealed class UnhandledExceptionObserverTests : IDisposable
{
    public UnhandledExceptionObserverTests()
    {
        UnhandledExceptionObserver.ResetSinkForTests();
    }

    public void Dispose()
    {
        UnhandledExceptionObserver.ResetSinkForTests();
    }

    [Fact]
    public void Report_InvokesConfiguredSink()
    {
        string? seenMessage = null;
        Exception? seenException = null;
        UnhandledExceptionObserver.Configure((message, exception) =>
        {
            seenMessage = message;
            seenException = exception;
        });

        var ex = new InvalidOperationException("boom");
        UnhandledExceptionObserver.Report("Dispatcher", ex);

        Assert.NotNull(seenMessage);
        Assert.Contains("Dispatcher", seenMessage, StringComparison.Ordinal);
        Assert.Contains("boom", seenMessage, StringComparison.Ordinal);
        Assert.Same(ex, seenException);
    }

    [Fact]
    public void Report_WithoutSink_DoesNotThrow()
    {
        UnhandledExceptionObserver.Report("AppDomain", new Exception("x"));
    }

    [Fact]
    public void Report_SinkThrowing_IsSwallowed()
    {
        UnhandledExceptionObserver.Configure((_, _) => throw new InvalidOperationException("sink failed"));
        UnhandledExceptionObserver.Report("UnobservedTask", new Exception("x"));
    }
}
