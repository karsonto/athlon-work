namespace Athlon.Agent.Core;

public sealed class SshWorkspaceSettings
{
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 22;

    public string Username { get; set; } = string.Empty;

    /// <summary>password | privateKey</summary>
    public string AuthMode { get; set; } = "password";

    /// <summary>Local path to a private key file when <see cref="AuthMode"/> is privateKey.</summary>
    public string? PrivateKeyPath { get; set; }

    public static string PasswordSecretName(string workspaceId) => $"ssh-password:{workspaceId}";

    public static string KeyPassphraseSecretName(string workspaceId) => $"ssh-key-passphrase:{workspaceId}";
}
