using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure.Ssh;

namespace Athlon.Agent.App.Services.Terminal;

/// <summary>
/// Serves one SSH handshake on a current-user named pipe.
/// The connect timeout starts when the terminal view launches the bridge.
/// </summary>
internal sealed class SshPtyHandshakeServer : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationTokenSource _timeout = new();
    private readonly Task _completed;
    private int _disposed;

    private SshPtyHandshakeServer(string pipeName, SshConnectRequest request)
    {
        PipeName = pipeName;
        _completed = Observe(ServeAsync(pipeName, request, _lifetime, _timeout));
    }

    public string PipeName { get; }

    public Task Completed => _completed;

    public static SshPtyHandshakeServer Start(SshConnectRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var pipeName = "athlon-ssh-pty-" + Guid.NewGuid().ToString("N");
        return new SshPtyHandshakeServer(pipeName, request);
    }

    public void RequireClientWithin(TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _timeout.CancelAfter(timeout);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already torn down.
        }

        _ = _completed.ContinueWith(
            static (task, state) =>
            {
                _ = task.Exception;
                var self = (SshPtyHandshakeServer)state!;
                self._lifetime.Dispose();
                self._timeout.Dispose();
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static Task Observe(Task completed) =>
        completed.ContinueWith(
            static task =>
            {
                if (task.IsFaulted)
                {
                    _ = task.Exception;
                }

                return task;
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default).Unwrap();

    private static async Task ServeAsync(
        string pipeName,
        SshConnectRequest request,
        CancellationTokenSource lifetime,
        CancellationTokenSource timeout)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, timeout.Token);
        var identity = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The current user has no security identifier.");
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            identity,
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        await using var server = NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.Out,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 64 * 1024,
            pipeSecurity: security);
        try
        {
            await server.WaitForConnectionAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("SSH terminal bridge did not connect.");
        }

        var frame = SshPtyHandshake.Encode(request);
        try
        {
            await server.WriteAsync(frame, linked.Token).ConfigureAwait(false);
            await server.FlushAsync(linked.Token).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(frame);
        }
    }
}
