using System.Security.Cryptography;
using System.Text;
using Athlon.Agent.Infrastructure.CloudSandbox;

namespace Athlon.Agent.Tests;

public sealed class AesGcmFrameTests
{
    [Fact]
    public void ParseSharedKey_RejectsWrongLength()
    {
        Assert.Throws<InvalidOperationException>(() =>
            AesGcmFrame.ParseSharedKeyBase64(Convert.ToBase64String(new byte[16])));
    }

    [Fact]
    public void SealOpen_RejectsTamperedFrame()
    {
        var key = AesGcmFrame.ParseSharedKeyBase64(Convert.ToBase64String(new byte[32]));
        var frame = AesGcmFrame.Seal(key, "payload"u8, "/m", "s");
        frame[^1] ^= 0xFF;
        Assert.ThrowsAny<CryptographicException>(() => AesGcmFrame.Open(key, frame, "/m", "s"));
    }

    [Fact]
    public void BuildAad_IncludesMethodAndSession()
    {
        var aad = AesGcmFrame.BuildAad("/athlon.cloud_sandbox.v1.CloudSandbox/Exec", "abc");
        Assert.Equal("/athlon.cloud_sandbox.v1.CloudSandbox/Exec|abc", Encoding.UTF8.GetString(aad));
    }
}
