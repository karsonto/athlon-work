namespace Athlon.Agent.Infrastructure;

public static class SensitiveText
{
    private static readonly string[] Tokens = { "Authorization", "api_key", "apikey", "token", "password", "secret" };

    public static string Redact(string message)
    {
        return Tokens.Aggregate(message, (current, token) =>
            current.Replace(token, $"{token}[redacted]", StringComparison.OrdinalIgnoreCase));
    }
}
