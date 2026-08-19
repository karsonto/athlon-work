using System.Threading;
using System.Threading.Tasks;
using Athlon.Agent.Core.RuntimeDiagnostics;

namespace Athlon.Agent.Tests;

internal sealed class NullRuntimeDiagnosticEventSink : IRuntimeDiagnosticEventSink
{
    public ValueTask EnqueueAsync(RuntimeDiagnosticEvent evt, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public ValueTask FlushAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}

