namespace Athlon.Agent.Core.Sso;

public sealed class NullCurrentSsoUserContext : ICurrentSsoUserContext
{
    public static NullCurrentSsoUserContext Instance { get; } = new();

    private NullCurrentSsoUserContext()
    {
    }

    public string? DisplayName => null;
}
