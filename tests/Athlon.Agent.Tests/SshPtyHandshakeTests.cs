using Athlon.Agent.Core;
using Athlon.Agent.Core.Terminal;
using Athlon.Agent.Infrastructure.Ssh;

namespace Athlon.Agent.Tests;

public sealed class PosixShellTests
{
    [Fact]
    public void Quote_WrapsPlainPath()
    {
        Assert.Equal("'/srv/app'", PosixShell.Quote("/srv/app"));
    }

    [Fact]
    public void Quote_EscapesEmbeddedSingleQuote()
    {
        Assert.Equal("'o'\"'\"'brien'", PosixShell.Quote("o'brien"));
    }

    [Fact]
    public void ChangeDirectory_KeepsTheShellWhenCdFails()
    {
        var command = PosixShell.ChangeDirectory("/srv/o'brien");
        Assert.Equal(
            "cd '/srv/o'\"'\"'brien' || echo 'athlon: failed to enter workspace directory'",
            command);
    }
}

public sealed class SshPtyHandshakeTests
{
    [Fact]
    public void BuildCommandLine_CarriesPipeNameAndNotThePassword()
    {
        const string password = "s3cret-password";
        var line = SshPtyHandshake.BuildCommandLine(
            @"C:\Program Files\Athlon\Athlon.Agent.SshPtyBridge.exe",
            "athlon-ssh-pty-abc");

        Assert.Contains("--pipe", line, StringComparison.Ordinal);
        Assert.Contains("athlon-ssh-pty-abc", line, StringComparison.Ordinal);
        Assert.Contains("\"C:\\Program Files\\Athlon\\Athlon.Agent.SshPtyBridge.exe\"", line, StringComparison.Ordinal);
        Assert.DoesNotContain(password, line, StringComparison.Ordinal);
    }

    [Fact]
    public void Encode_RoundTripsCredentialsThatStayOffTheCommandLine()
    {
        const string password = "s3cret-password";
        var request = new SshConnectRequest(
            "ws-1",
            "shell.example",
            22,
            "dev",
            "/srv/app",
            "password",
            null,
            password,
            null);
        var line = SshPtyHandshake.BuildCommandLine(
            @"C:\Athlon\Athlon.Agent.SshPtyBridge.exe",
            "athlon-ssh-pty-abc");
        var frame = SshPtyHandshake.Encode(request);

        Assert.DoesNotContain(password, line, StringComparison.Ordinal);
        var decoded = SshPtyHandshake.Decode(frame);
        Assert.Equal(password, decoded.Password);
        Assert.Equal("shell.example", decoded.Host);
        Assert.Equal("/srv/app", decoded.RemoteRoot);
        Assert.Equal("dev", decoded.Username);
    }

    [Fact]
    public void TryGetPipeName_RejectsPathSeparators()
    {
        Assert.True(SshPtyHandshake.TryGetPipeName(["--pipe", "athlon-ssh-pty-abc"], out var pipe));
        Assert.Equal("athlon-ssh-pty-abc", pipe);
        Assert.False(SshPtyHandshake.TryGetPipeName(["--pipe", "..\\secret"], out _));
    }
}

public sealed class TerminalEnterEncodingTests
{
    [Fact]
    public void EnterSequence_LocalUsesWin32KeyEvents()
    {
        var sequence = TerminalEnterEncoding.EnterSequence(useWin32Enter: true);
        Assert.Contains(TerminalEnterEncoding.Win32EnterKeyDown, sequence, StringComparison.Ordinal);
        Assert.Contains(TerminalEnterEncoding.Win32EnterKeyUp, sequence, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", sequence, StringComparison.Ordinal);
    }

    [Fact]
    public void EnterSequence_RemoteIsCarriageReturn()
    {
        Assert.Equal("\r", TerminalEnterEncoding.EnterSequence(useWin32Enter: false));
    }
}
