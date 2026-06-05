using System.Diagnostics;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class WindowsCmdEncodingTests
{
    [Fact]
    public void ApplyTo_SetsUtf8Encodings_AndForcesPythonUtf8()
    {
        var startInfo = new ProcessStartInfo("cmd.exe", "/c echo hello")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false
        };

        WindowsCmdEncoding.ApplyTo(startInfo);

        Assert.Equal("utf-8", startInfo.Environment["PYTHONIOENCODING"]);
        Assert.Equal("1", startInfo.Environment["PYTHONUTF8"]);
        Assert.NotNull(startInfo.StandardInputEncoding);
        Assert.NotNull(startInfo.StandardOutputEncoding);
        Assert.NotNull(startInfo.StandardErrorEncoding);
        Assert.Equal("utf-8", startInfo.StandardInputEncoding!.WebName);
        Assert.Equal("utf-8", startInfo.StandardOutputEncoding!.WebName);
        Assert.Equal("utf-8", startInfo.StandardErrorEncoding!.WebName);
    }
}

