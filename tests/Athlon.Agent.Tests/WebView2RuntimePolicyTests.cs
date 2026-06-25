using Athlon.Agent.App.Services;

namespace Athlon.Agent.Tests;

public sealed class WebView2RuntimePolicyTests
{
    [Theory]
    [InlineData(19045, true)]
    [InlineData(21999, true)]
    [InlineData(22000, false)]
    [InlineData(22631, false)]
    public void ShouldUseBundledRuntime_MatchesWindowsBuildThreshold(int osBuild, bool expected) =>
        Assert.Equal(expected, WebView2RuntimePolicy.ShouldUseBundledRuntime(osBuild));
}
