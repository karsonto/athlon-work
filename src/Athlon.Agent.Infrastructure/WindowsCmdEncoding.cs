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
        // When stdout/stderr are redirected (pipes), Python defaults to the Windows ANSI codepage
        // (often GBK/CP936 on zh-CN) which cannot encode many Unicode characters (e.g. ✅).
        // Force UTF-8 for Python so tool output is stable across hosts.
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        startInfo.Environment["PYTHONUTF8"] = "1";

        startInfo.StandardInputEncoding = Encoding.UTF8;
        startInfo.StandardOutputEncoding = Encoding.UTF8;
        startInfo.StandardErrorEncoding = Encoding.UTF8;
    }
}
