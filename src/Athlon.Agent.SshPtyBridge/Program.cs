using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure.Ssh;
using Renci.SshNet;

namespace Athlon.Agent.SshPtyBridge;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (!SshPtyHandshake.TryGetPipeName(args, out var pipeName))
            {
                Console.Error.WriteLine("Usage: Athlon.Agent.SshPtyBridge --pipe <name>");
                return 1;
            }

            var request = ReadHandshake(pipeName);
            using var ssh = SshShellClientFactory.Connect(request);
            var (columns, rows) = ConsoleSize.Read();
            using var shell = ssh.CreateShellStream(
                "xterm-256color",
                columns,
                rows,
                columns * 8,
                rows * 16,
                32 * 1024);
            var cd = PosixShell.ChangeDirectory(
                string.IsNullOrWhiteSpace(request.RemoteRoot) ? "/" : request.RemoteRoot);
            var cdBytes = Encoding.UTF8.GetBytes(cd + "\r");
            shell.Write(cdBytes, 0, cdBytes.Length);
            shell.Flush();

            ConsoleMode.DisableProcessedInput();
            using var stop = new CancellationTokenSource();
            var resize = Task.Run(() => WatchSize(shell, columns, rows, stop.Token));
            var incoming = Task.Run(() => PumpShellToConsole(shell));
            try
            {
                PumpConsoleToShell(shell);
            }
            finally
            {
                stop.Cancel();
                try
                {
                    shell.Dispose();
                }
                catch
                {
                    // The read loop unblocks when the channel closes.
                }

                try
                {
                    ssh.Disconnect();
                }
                catch
                {
                    // Best-effort teardown.
                }

                try
                {
                    incoming.Wait(TimeSpan.FromSeconds(2));
                }
                catch
                {
                    // The output pump ends with the shell.
                }

                try
                {
                    resize.Wait(TimeSpan.FromSeconds(2));
                }
                catch
                {
                    // The resize loop ends when stop is cancelled.
                }
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static SshConnectRequest ReadHandshake(string pipeName)
    {
        using var pipe = new System.IO.Pipes.NamedPipeClientStream(
            ".",
            pipeName,
            System.IO.Pipes.PipeDirection.In);
        pipe.Connect(5000);
        using var buffer = new MemoryStream();
        pipe.CopyTo(buffer);
        var frame = buffer.GetBuffer();
        var length = (int)buffer.Length;
        try
        {
            return SshPtyHandshake.Decode(frame.AsSpan(0, length));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(frame.AsSpan(0, length));
        }
    }

    private static void PumpConsoleToShell(ShellStream shell)
    {
        var input = ConsoleHandles.GetStdHandle(ConsoleHandles.StdInput);
        var buffer = new byte[4096];
        while (true)
        {
            if (!ConsoleHandles.ReadFile(input, buffer, (uint)buffer.Length, out var read, IntPtr.Zero) || read == 0)
            {
                return;
            }

            shell.Write(buffer, 0, (int)read);
            shell.Flush();
        }
    }

    private static void PumpShellToConsole(ShellStream shell)
    {
        var output = ConsoleHandles.GetStdHandle(ConsoleHandles.StdOutput);
        var buffer = new byte[8192];
        try
        {
            while (true)
            {
                var read = shell.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    return;
                }

                if (!ConsoleHandles.WriteAll(output, buffer, read))
                {
                    return;
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // Stdin closed and the shell was disposed.
        }
        catch (IOException)
        {
            // The channel closed.
        }
    }

    private static void WatchSize(ShellStream shell, uint columns, uint rows, CancellationToken cancellationToken)
    {
        var lastColumns = columns;
        var lastRows = rows;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                cancellationToken.WaitHandle.WaitOne(200);
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var (nextColumns, nextRows) = ConsoleSize.Read();
            if (nextColumns == lastColumns && nextRows == lastRows)
            {
                continue;
            }

            TerminalResize.TrySend(shell, nextColumns, nextRows);
            lastColumns = nextColumns;
            lastRows = nextRows;
        }
    }
}

internal static class ConsoleSize
{
    public static (uint Columns, uint Rows) Read()
    {
        try
        {
            var columns = Math.Clamp(Console.WindowWidth, 1, 500);
            var rows = Math.Clamp(Console.WindowHeight, 1, 200);
            return ((uint)columns, (uint)rows);
        }
        catch
        {
            return (120, 30);
        }
    }
}

internal static class ConsoleMode
{
    private const uint EnableProcessedInput = 0x0001;
    private const uint EnableLineInput = 0x0002;
    private const uint EnableEchoInput = 0x0004;

    public static void DisableProcessedInput()
    {
        try
        {
            Console.TreatControlCAsInput = true;
        }
        catch
        {
            // No console yet; the mode call below is the one that matters.
        }

        var input = ConsoleHandles.GetStdHandle(ConsoleHandles.StdInput);
        if (input == IntPtr.Zero || input == new IntPtr(-1))
        {
            return;
        }

        if (!GetConsoleMode(input, out var mode))
        {
            return;
        }

        mode &= ~EnableProcessedInput;
        mode &= ~EnableLineInput;
        mode &= ~EnableEchoInput;
        SetConsoleMode(input, mode);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr handle, out uint mode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr handle, uint mode);
}

internal static class TerminalResize
{
    public static void TrySend(ShellStream shell, uint columns, uint rows)
    {
        try
        {
            var method = shell.GetType().GetMethod("SendWindowChangeRequest");
            if (method is not null)
            {
                method.Invoke(shell, [columns, rows, columns * 8, rows * 16]);
                return;
            }

            var channel = shell.GetType()
                .GetField("_channel", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.GetValue(shell);
            var channelMethod = channel?.GetType().GetMethod("SendWindowChangeRequest");
            channelMethod?.Invoke(channel, [columns, rows, columns * 8, rows * 16]);
        }
        catch
        {
            // Keep the size negotiated when the shell was opened.
        }
    }
}

internal static class ConsoleHandles
{
    public const int StdInput = -10;
    public const int StdOutput = -11;

    public static bool WriteAll(IntPtr handle, byte[] buffer, int count)
    {
        var pending = new byte[count];
        Buffer.BlockCopy(buffer, 0, pending, 0, count);
        var offset = 0;
        while (offset < count)
        {
            var sliceLength = count - offset;
            var slice = pending;
            if (offset != 0)
            {
                slice = new byte[sliceLength];
                Buffer.BlockCopy(pending, offset, slice, 0, sliceLength);
            }

            if (!WriteFile(handle, slice, (uint)sliceLength, out var written, IntPtr.Zero) || written == 0)
            {
                return false;
            }

            offset += (int)written;
        }

        return true;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GetStdHandle(int handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadFile(
        IntPtr handle,
        byte[] buffer,
        uint bytesToRead,
        out uint bytesRead,
        IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(
        IntPtr handle,
        byte[] buffer,
        uint bytesToWrite,
        out uint bytesWritten,
        IntPtr overlapped);
}
