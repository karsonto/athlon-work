using Athlon.Agent.Core;
using Renci.SshNet;

namespace Athlon.Agent.Infrastructure.Ssh;

/// <summary>Opens a dedicated SSH connection for an interactive shell. Does not open SFTP.</summary>
public static class SshShellClientFactory
{
    public static SshClient Connect(SshConnectRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var ssh = new SshClient(SshConnectionFactory.BuildConnectionInfo(request))
        {
            KeepAliveInterval = SshConnectionFactory.KeepAliveInterval
        };
        ssh.Connect();
        return ssh;
    }
}
