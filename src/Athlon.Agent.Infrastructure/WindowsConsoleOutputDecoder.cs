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

    internal static string DecodeLine(ReadOnlySpan<byte> lineBytes) =>
        DecodeLine(lineBytes, SystemAnsiEncoding);

    internal static string DecodeLine(ReadOnlySpan<byte> lineBytes, Encoding? ansiEncoding)
    {
        if (lineBytes.IsEmpty)
        {
            return string.Empty;
        }

        lineBytes = StripUtf8Bom(lineBytes);

        var utf8Ok = TryDecodeStrictUtf8(lineBytes, out var utf8Text);
        var ansiOk = TryDecodeStrict(ansiEncoding, lineBytes, out var ansiText);
        if (utf8Ok && ansiOk)
        {
            // UTF-8 Han is 3 bytes per character and GBK is 2, so a GBK read of UTF-8
            // Chinese yields more characters and a higher per-character score ("完成" → "瀹屾垚").
            // A longer ANSI decoding is that split. Equal length still uses the score, so a
            // GBK character whose bytes happen to be valid UTF-8 ("山" → "ɽ") stays GBK.
            if (ansiText.Length > utf8Text.Length)
            {
                return utf8Text;
            }

            return ScoreDecodedText(ansiText) > ScoreDecodedText(utf8Text) ? ansiText : utf8Text;
        }

        if (utf8Ok)
        {
            return utf8Text;
        }

        if (ansiOk)
        {
            return ansiText;
        }

        return Encoding.UTF8.GetString(lineBytes);
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

    private static bool TryDecodeStrict(Encoding? encoding, ReadOnlySpan<byte> lineBytes, out string text)
    {
        text = string.Empty;
        if (encoding is null)
        {
            return false;
        }

        try
        {
            text = AsStrict(encoding).GetString(lineBytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }
    }

    private static Encoding AsStrict(Encoding encoding)
    {
        if (encoding.DecoderFallback is DecoderExceptionFallback
            && encoding.EncoderFallback is EncoderExceptionFallback)
        {
            return encoding;
        }

        return Encoding.GetEncoding(
            encoding.CodePage,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);
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
            // Strict decoding: invalid bytes must not become U+FFFD and then win on length.
            var acp = Encoding.GetEncoding(0);
            return Encoding.GetEncoding(
                acp.CodePage,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }
}
