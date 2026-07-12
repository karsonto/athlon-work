using System.Text;

namespace Athlon.Agent.Infrastructure;

/// <summary>
/// Decodes raw Windows console bytes. Child processes may emit UTF-8 (after chcp 65001)
/// or system ANSI (e.g. GBK/CP936) for cmd.exe built-in messages.
/// </summary>
internal static class WindowsConsoleOutputDecoder
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly Encoding? SystemAnsiEncoding;

    static WindowsConsoleOutputDecoder()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        SystemAnsiEncoding = CreateSystemAnsiEncoding();
    }

    internal static string DecodeLine(ReadOnlySpan<byte> lineBytes)
    {
        if (lineBytes.IsEmpty)
        {
            return string.Empty;
        }

        lineBytes = StripUtf8Bom(lineBytes);

        string? bestText = null;
        var bestScore = int.MinValue;

        if (TryDecodeStrictUtf8(lineBytes, out var utf8Text))
        {
            var score = ScoreDecodedText(utf8Text);
            if (score > bestScore)
            {
                bestScore = score;
                bestText = utf8Text;
            }
        }

        if (SystemAnsiEncoding is not null)
        {
            var ansiText = SystemAnsiEncoding.GetString(lineBytes);
            var score = ScoreDecodedText(ansiText);
            if (score > bestScore)
            {
                bestScore = score;
                bestText = ansiText;
            }
        }

        return bestText ?? Encoding.UTF8.GetString(lineBytes);
    }

    private static ReadOnlySpan<byte> StripUtf8Bom(ReadOnlySpan<byte> lineBytes) =>
        lineBytes.Length >= 3
        && lineBytes[0] == 0xEF
        && lineBytes[1] == 0xBB
        && lineBytes[2] == 0xBF
            ? lineBytes[3..]
            : lineBytes;

    private static bool TryDecodeStrictUtf8(ReadOnlySpan<byte> lineBytes, out string text)
    {
        try
        {
            text = StrictUtf8.GetString(lineBytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }
    }

    private static int ScoreDecodedText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return int.MinValue / 2;
        }

        var score = 0;
        foreach (var ch in text)
        {
            score += ch switch
            {
                '\uFFFD' => -20,
                >= '\u4E00' and <= '\u9FFF' => 3,
                >= ' ' and <= '~' => 1,
                '\r' or '\n' or '\t' => 0,
                < ' ' => -2,
                _ => -1
            };
        }

        return score;
    }

    private static Encoding? CreateSystemAnsiEncoding()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            // Code page 0 is the system default ANSI code page (ACP), which matches
            // cmd.exe built-in messages regardless of the current thread culture.
            return Encoding.GetEncoding(0);
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }
}
