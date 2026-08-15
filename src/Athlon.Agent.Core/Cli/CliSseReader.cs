using System.Runtime.CompilerServices;
using System.Text;

namespace Athlon.Agent.Core.Cli;

public sealed record CliParsedSse(string Event, string Data);

public static class CliSseReader
{
    public static async IAsyncEnumerable<CliParsedSse> ReadAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
        string? eventName = null;
        var data = new StringBuilder();

        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                if (eventName is not null || data.Length > 0)
                {
                    yield return new CliParsedSse(eventName ?? "message", data.ToString());
                }

                yield break;
            }

            if (line.Length == 0)
            {
                if (eventName is not null || data.Length > 0)
                {
                    yield return new CliParsedSse(eventName ?? "message", data.ToString());
                }

                eventName = null;
                data.Clear();
                continue;
            }

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventName = line["event:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var payload = line["data:".Length..].TrimStart();
                if (data.Length > 0)
                {
                    data.Append('\n');
                }

                data.Append(payload);
            }
        }
    }
}
