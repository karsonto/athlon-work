namespace Athlon.Agent.Core.Sso;

using System.Text.Json.Serialization;

public sealed class ImpSsoCallbackPayload
{
    public string? AppId { get; init; }

    public string? UserId { get; init; }

    public string? Locale { get; init; }

    public string? Token { get; init; }

    public string? Role { get; init; }

    public string? Depname { get; init; }

    [JsonPropertyName("channel_type")]
    public string? ChannelType { get; init; }

    public string? Msg { get; init; }
}
