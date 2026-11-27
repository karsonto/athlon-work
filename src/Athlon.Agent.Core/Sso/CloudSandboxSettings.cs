namespace Athlon.Agent.Core.Sso;

/// <summary>Remote room sandbox control-plane settings (desktop client).</summary>
public sealed class CloudSandboxSettings
{
    /// <summary>
    /// gRPC endpoint base URL (e.g. <c>http://cloud-sandbox.corp.local:9090</c>).
    /// HTTP/2 cleartext (h2c) is supported; payloads are AES-GCM sealed with <see cref="SharedKey"/>.
    /// </summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>API key for caller identity / enterprise metering metadata.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Base64-encoded 32-byte AES-256 shared key for application-layer encryption.</summary>
    public string SharedKey { get; set; } = "";

    /// <summary>Runtime image tag requested when opening a session (short tag; server may prefix registry).</summary>
    public string ImageTag { get; set; } = "athlon-room-sandbox:2";

    /// <summary>Preferred region id (P2). Empty = server default.</summary>
    public string PreferredRegion { get; set; } = "";

    /// <summary>HttpsEgress | VpcOnly</summary>
    public string NetworkPolicy { get; set; } = "HttpsEgress";

    public int RequestTimeoutSeconds { get; set; } = 180;

    /// <summary>Prefer manifest/delta sync; fall back to full tar.gz when unsupported.</summary>
    public bool PreferIncrementalSync { get; set; } = true;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl)
        && Uri.TryCreate(BaseUrl.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && !string.IsNullOrWhiteSpace(ApiKey)
        && !string.IsNullOrWhiteSpace(SharedKey);
}
