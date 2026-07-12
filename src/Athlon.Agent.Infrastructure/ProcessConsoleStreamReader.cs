using System.Buffers;

namespace Athlon.Agent.Infrastructure;

/// <summary>Reads a redirected process stream line-by-line from raw bytes.</summary>
internal static class ProcessConsoleStreamReader
{
    internal static async Task ReadLinesAsync(
        Stream stream,
        Action<string> onLine,
        CancellationToken cancellationToken)
    {
        var lineBuffer = new ArrayBufferWriter<byte>(256);
        var readBuffer = new byte[4096];

        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(readBuffer.AsMemory(0, readBuffer.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                for (var i = 0; i < read; i++)
                {
                    var b = readBuffer[i];
                    if (b == (byte)'\n')
                    {
                        EmitLine(lineBuffer, onLine);
                        lineBuffer.Clear();
                        continue;
                    }

                    lineBuffer.GetSpan(1)[0] = b;
                    lineBuffer.Advance(1);
                }
            }

            if (lineBuffer.WrittenCount > 0)
            {
                EmitLine(lineBuffer, onLine);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when user cancels or timeout fires.
        }
    }

    private static void EmitLine(ArrayBufferWriter<byte> lineBuffer, Action<string> onLine)
    {
        var span = lineBuffer.WrittenSpan;
        if (span.Length > 0 && span[^1] == (byte)'\r')
        {
            span = span[..^1];
        }

        onLine(WindowsConsoleOutputDecoder.DecodeLine(span));
    }
}
