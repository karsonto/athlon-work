using Athlon.Agent.Core.Cli;

namespace Athlon.Agent.Tests;

public sealed class CliEndpointFileTests
{
    [Fact]
    public void TryGetLive_MissingFile_ReturnsNull()
    {
        using var temp = new TempDirectoryScope("cli-endpoint");
        Assert.Null(CliEndpointFile.TryGetLive(temp.Root));
    }

    [Fact]
    public void TryGetLive_DeadPid_ReturnsNull()
    {
        using var temp = new TempDirectoryScope("cli-endpoint");
        CliEndpointFile.Write(temp.Root, new CliEndpointInfo
        {
            Url = "http://127.0.0.1:1/",
            Token = "abc",
            Pid = int.MaxValue
        });

        Assert.Null(CliEndpointFile.TryGetLive(temp.Root));
    }

    [Fact]
    public void TryGetLive_CurrentProcess_ReturnsEndpoint()
    {
        using var temp = new TempDirectoryScope("cli-endpoint");
        var info = new CliEndpointInfo
        {
            Url = "http://127.0.0.1:9/",
            Token = "token",
            Pid = Environment.ProcessId
        };
        CliEndpointFile.Write(temp.Root, info);

        var live = CliEndpointFile.TryGetLive(temp.Root);
        Assert.NotNull(live);
        Assert.Equal(info.Url, live.Url);
        Assert.Equal(info.Token, live.Token);
        Assert.Equal(info.Pid, live.Pid);
    }
}
