namespace Athlon.Agent.Core.Sso;

public sealed class ImpSsoSession
{
    public string SsoToken { get; init; } = "";

    public string UserId { get; init; } = "";

    public string DisplayName { get; init; } = "";

    public DateTimeOffset LoggedInAt { get; init; }

    public DateTimeOffset ExpiresAt { get; init; }

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;
}
