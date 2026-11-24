namespace Athlon.Agent.Core.Sso;

/// <summary>Remote room sandbox control-plane settings (desktop client).</summary>
public sealed class CloudSandboxSettings
{
    /// <summary>Control API base URL. Empty = cloud sandbox unavailable.</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>Runtime image tag requested when opening a session.</summary>
    public string ImageTag { get; set; } = "athlon-room-sandbox:2";

    public int RequestTimeoutSeconds { get; set; } = 180;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl)
        && Uri.TryCreate(BaseUrl.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
}
