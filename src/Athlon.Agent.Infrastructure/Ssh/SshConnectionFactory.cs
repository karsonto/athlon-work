using Athlon.Agent.Core;
using Renci.SshNet;

namespace Athlon.Agent.Infrastructure.Ssh;

/// <summary>
/// Builds and connects SSH/SFTP client pairs from an <see cref="SshConnectRequest"/>.
/// Shared by per-session connection slots and one-shot connection probes (wizard test,
/// edit-window test) so every connection uses identical auth/timeout/keep-alive settings.
/// </summary>
internal static class SshConnectionFactory
{
    public const int ConnectTimeoutSeconds = 30;
    public static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(30);

    public static (SshClient Ssh, SftpClient Sftp) CreateClients(SshConnectRequest request)
    {
        var connection = BuildConnectionInfo(request);
        var ssh = new SshClient(connection);
        var sftp = new SftpClient(connection);
        ssh.KeepAliveInterval = KeepAliveInterval;
        sftp.KeepAliveInterval = KeepAliveInterval;
        return (ssh, sftp);
    }

    public static ConnectionInfo BuildConnectionInfo(SshConnectRequest request) =>
        new(request.Host, request.Port <= 0 ? 22 : request.Port, request.Username, BuildAuthMethods(request))
        {
            Timeout = TimeSpan.FromSeconds(ConnectTimeoutSeconds)
        };

    /// <summary>Connects both clients, honoring cancellation between the two blocking connects.</summary>
    public static void ConnectPair(SshClient ssh, SftpClient sftp, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ssh.Connect();
        cancellationToken.ThrowIfCancellationRequested();
        sftp.Connect();
    }

    public static void SafeDispose(IDisposable? ssh, IDisposable? sftp)
    {
        try
        {
            sftp?.Dispose();
        }
        catch
        {
            // Best-effort cleanup.
        }

        try
        {
            ssh?.Dispose();
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    public static AuthenticationMethod[] BuildAuthMethods(SshConnectRequest request)
    {
        if (string.Equals(request.AuthMode, "privateKey", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(request.PrivateKeyPath) || !File.Exists(request.PrivateKeyPath))
            {
                throw new InvalidOperationException("Private key file was not found.");
            }

            PrivateKeyFile keyFile = string.IsNullOrEmpty(request.PrivateKeyPassphrase)
                ? new PrivateKeyFile(request.PrivateKeyPath)
                : new PrivateKeyFile(request.PrivateKeyPath, request.PrivateKeyPassphrase);
            return [new PrivateKeyAuthenticationMethod(request.Username, keyFile)];
        }

        return [new PasswordAuthenticationMethod(request.Username, request.Password ?? string.Empty)];
    }
}
