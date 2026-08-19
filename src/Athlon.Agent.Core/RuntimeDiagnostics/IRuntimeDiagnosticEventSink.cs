using System.Threading;
using System.Threading.Tasks;

namespace Athlon.Agent.Core.RuntimeDiagnostics;

public interface IRuntimeDiagnosticEventSink
{
    ValueTask EnqueueAsync(RuntimeDiagnosticEvent evt, CancellationToken cancellationToken = default);

    ValueTask FlushAsync(CancellationToken cancellationToken = default);
}

