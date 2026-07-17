using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

public sealed class RemotePathNormalizerTests
{
    [Fact]
    public void NormalizeRoot_TrimsTrailingSlashAndCollapses()
    {
        Assert.Equal("/home/u/proj", RemotePathNormalizer.NormalizeRoot("/home/u/proj/"));
        Assert.Equal("/home/u/proj", RemotePathNormalizer.NormalizeRoot(@"\home\u\proj"));
        Assert.Equal("/a/b", RemotePathNormalizer.NormalizeRoot("/a/./b/../b"));
    }

    [Fact]
    public void Combine_ResolvesRelativeAgainstRoot()
    {
        Assert.Equal("/home/u/proj/src/a.cs", RemotePathNormalizer.Combine("/home/u/proj", "src/a.cs"));
        Assert.Equal("/etc/hosts", RemotePathNormalizer.Combine("/home/u/proj", "/etc/hosts"));
    }

    [Fact]
    public void IsUnderRoot_RejectsEscape()
    {
        Assert.True(RemotePathNormalizer.IsUnderRoot("/home/u/proj/src", "/home/u/proj"));
        Assert.False(RemotePathNormalizer.IsUnderRoot("/home/other", "/home/u/proj"));
        Assert.False(RemotePathNormalizer.IsUnderRoot("/home/u/proj/../other", "/home/u/proj"));
    }
}
