using Athlon.Agent.CloudSandbox.V1;
using Google.Protobuf;

namespace Athlon.Agent.Infrastructure.CloudSandbox;

public static class CloudSandboxRpcs
{
    public const string Status = "/athlon.cloud_sandbox.v1.CloudSandbox/Status";
    public const string ListRegions = "/athlon.cloud_sandbox.v1.CloudSandbox/ListRegions";
    public const string CreateSession = "/athlon.cloud_sandbox.v1.CloudSandbox/CreateSession";
    public const string DeleteSession = "/athlon.cloud_sandbox.v1.CloudSandbox/DeleteSession";
    public const string SyncManifest = "/athlon.cloud_sandbox.v1.CloudSandbox/SyncManifest";
    public const string SyncDelta = "/athlon.cloud_sandbox.v1.CloudSandbox/SyncDelta";
    public const string SyncFull = "/athlon.cloud_sandbox.v1.CloudSandbox/SyncFull";
    public const string PullWorkspace = "/athlon.cloud_sandbox.v1.CloudSandbox/PullWorkspace";
    public const string Exec = "/athlon.cloud_sandbox.v1.CloudSandbox/Exec";
    public const string ReadFile = "/athlon.cloud_sandbox.v1.CloudSandbox/ReadFile";
    public const string WriteFile = "/athlon.cloud_sandbox.v1.CloudSandbox/WriteFile";
    public const string PathStat = "/athlon.cloud_sandbox.v1.CloudSandbox/PathStat";
    public const string ListDir = "/athlon.cloud_sandbox.v1.CloudSandbox/ListDir";
    public const string MkDir = "/athlon.cloud_sandbox.v1.CloudSandbox/MkDir";
}

public static class CloudSandboxSecure
{
    public static SecureEnvelope Seal(
        byte[] key,
        IMessage message,
        string rpcMethod,
        string? sessionId = null)
    {
        var frame = AesGcmFrame.Seal(key, message.ToByteArray(), rpcMethod, sessionId);
        return new SecureEnvelope { Frame = ByteString.CopyFrom(frame) };
    }

    public static T Open<T>(
        byte[] key,
        SecureEnvelope envelope,
        MessageParser<T> parser,
        string rpcMethod,
        string? sessionId = null)
        where T : IMessage<T>
    {
        var plain = AesGcmFrame.Open(key, envelope.Frame.Span, rpcMethod, sessionId);
        return parser.ParseFrom(plain);
    }
}
