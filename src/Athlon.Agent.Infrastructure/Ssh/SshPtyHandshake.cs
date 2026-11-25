using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure.Ssh;

/// <summary>
/// One-shot handshake for the SSH terminal bridge.
/// The command line carries only a pipe name. Credentials travel in the pipe frame.
/// </summary>
public static class SshPtyHandshake
{
    public const string PipeArgument = "--pipe";
    public const int MaxFrameBytes = 1024 * 1024;

    public static string BuildCommandLine(string bridgeExecutable, string pipeName)
    {
        if (string.IsNullOrWhiteSpace(bridgeExecutable))
        {
            throw new ArgumentException("Bridge executable is required.", nameof(bridgeExecutable));
        }

        if (string.IsNullOrWhiteSpace(pipeName) || pipeName.IndexOfAny(['\\', '/', '"']) >= 0)
        {
            throw new ArgumentException("Pipe name is invalid.", nameof(pipeName));
        }

        return QuoteWindowsArgument(bridgeExecutable) + " " + PipeArgument + " " + QuoteWindowsArgument(pipeName);
    }

    public static bool TryGetPipeName(string[] args, out string pipeName)
    {
        pipeName = string.Empty;
        if (args is null)
        {
            return false;
        }

        for (var index = 0; index < args.Length - 1; index++)
        {
            if (!string.Equals(args[index], PipeArgument, StringComparison.Ordinal))
            {
                continue;
            }

            var candidate = args[index + 1];
            if (string.IsNullOrWhiteSpace(candidate) || candidate.IndexOfAny(['\\', '/', '"']) >= 0)
            {
                return false;
            }

            pipeName = candidate;
            return true;
        }

        return false;
    }

    public static byte[] Encode(SshConnectRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var json = JsonSerializer.SerializeToUtf8Bytes(request);
        try
        {
            if (json.Length is <= 0 or > MaxFrameBytes)
            {
                throw new InvalidOperationException("SSH terminal handshake is too large.");
            }

            var frame = new byte[4 + json.Length];
            BinaryPrimitives.WriteInt32LittleEndian(frame, json.Length);
            json.CopyTo(frame.AsSpan(4));
            return frame;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(json);
        }
    }

    public static SshConnectRequest Decode(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 4)
        {
            throw new InvalidOperationException("SSH terminal handshake is incomplete.");
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(frame);
        if (length <= 0 || length > MaxFrameBytes || frame.Length < 4 + length)
        {
            throw new InvalidOperationException("SSH terminal handshake is invalid.");
        }

        var request = JsonSerializer.Deserialize<SshConnectRequest>(frame.Slice(4, length))
            ?? throw new InvalidOperationException("SSH terminal handshake is empty.");
        if (string.IsNullOrWhiteSpace(request.Host) || string.IsNullOrWhiteSpace(request.Username))
        {
            throw new InvalidOperationException("SSH terminal handshake is missing the host or username.");
        }

        return request;
    }

    internal static string QuoteWindowsArgument(string value)
    {
        if (value.Length == 0)
        {
            return "\"\"";
        }

        var needsQuote = false;
        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch) || ch == '"')
            {
                needsQuote = true;
                break;
            }
        }

        if (!needsQuote)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        var backslashes = 0;
        foreach (var ch in value)
        {
            if (ch == '\\')
            {
                backslashes++;
                continue;
            }

            if (ch == '"')
            {
                builder.Append('\\', (backslashes * 2) + 1);
                builder.Append('"');
                backslashes = 0;
                continue;
            }

            if (backslashes > 0)
            {
                builder.Append('\\', backslashes);
                backslashes = 0;
            }

            builder.Append(ch);
        }

        if (backslashes > 0)
        {
            builder.Append('\\', backslashes * 2);
        }

        builder.Append('"');
        return builder.ToString();
    }
}
