using System.Diagnostics;
using System.Text;

namespace Athlon.Agent.Infrastructure;

/// <summary>UTF-8 console setup for cmd.exe child processes on Windows.</summary>
internal static class WindowsCmdEncoding
{
    private const string Utf8CodePagePrefix = "chcp 65001 >nul && ";

    internal static string WrapCommandForUtf8(string command) =>
        command.Contains("chcp 65001", StringComparison.OrdinalIgnoreCase)
            ? command
            : Utf8CodePagePrefix + command;

    internal static void ApplyTo(ProcessStartInfo startInfo)
    {
        startInfo.StandardOutputEncoding = Encoding.UTF8;
        startInfo.StandardErrorEncoding = Encoding.UTF8;
    }
}
