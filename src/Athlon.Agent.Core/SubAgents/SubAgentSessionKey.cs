namespace Athlon.Agent.Core.SubAgents;

public static class SubAgentSessionKey
{
    private const string Prefix = "sub:";

    public static string Build(string parentSessionId, string subSessionId) =>
        $"{Prefix}{parentSessionId}:{subSessionId}";

    public static bool TryParse(string sessionKey, out string parentSessionId, out string subSessionId)
    {
        parentSessionId = string.Empty;
        subSessionId = string.Empty;
        if (string.IsNullOrWhiteSpace(sessionKey) || !sessionKey.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var body = sessionKey[Prefix.Length..];
        var separator = body.IndexOf(':');
        if (separator <= 0 || separator >= body.Length - 1)
        {
            return false;
        }

        parentSessionId = body[..separator];
        subSessionId = body[(separator + 1)..];
        return !string.IsNullOrWhiteSpace(parentSessionId) && !string.IsNullOrWhiteSpace(subSessionId);
    }
}
