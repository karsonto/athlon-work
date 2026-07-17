using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure.Ssh;

public sealed class SshWorkspaceConnectionService(
    ISshWorkspaceClient client,
    ICredentialStore credentialStore,
    IAppLogger logger)
{
    private readonly IAppLogger _logger = logger.ForContext("SshWorkspaceConnectionService");

    public async Task SyncAsync(AgentSession session, AppSettings settings, CancellationToken cancellationToken = default)
    {
        var match = WorkspaceSessionResolver.FindMatch(session, settings);
        if (match is null || match.WorkspaceKind != WorkspaceKind.Ssh || match.Ssh is null)
        {
            if (client.IsConnected)
            {
                await client.DisconnectAsync(cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        try
        {
            var request = await BuildRequestAsync(match, cancellationToken).ConfigureAwait(false);
            await client.ConnectAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning("Failed to connect SSH workspace id={WorkspaceId}: {Message}", match.Id, ex.Message);
            throw;
        }
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default) =>
        client.DisconnectAsync(cancellationToken);

    public async Task<SshConnectRequest> BuildRequestAsync(WorkspaceSettings workspace, CancellationToken cancellationToken = default)
    {
        if (workspace.Ssh is null)
        {
            throw new InvalidOperationException("SSH settings are missing.");
        }

        var ssh = workspace.Ssh;
        string? password = null;
        string? passphrase = null;
        if (string.Equals(ssh.AuthMode, "privateKey", StringComparison.OrdinalIgnoreCase))
        {
            passphrase = await credentialStore
                .GetSecretAsync(SshWorkspaceSettings.KeyPassphraseSecretName(workspace.Id), cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            password = await credentialStore
                .GetSecretAsync(SshWorkspaceSettings.PasswordSecretName(workspace.Id), cancellationToken)
                .ConfigureAwait(false);
        }

        return new SshConnectRequest(
            workspace.Id,
            ssh.Host.Trim(),
            ssh.Port <= 0 ? 22 : ssh.Port,
            ssh.Username.Trim(),
            string.IsNullOrWhiteSpace(workspace.RootPath) ? "/" : workspace.RootPath,
            string.IsNullOrWhiteSpace(ssh.AuthMode) ? "password" : ssh.AuthMode,
            ssh.PrivateKeyPath,
            password,
            passphrase);
    }

    public async Task TestConnectionAsync(WorkspaceSettings workspace, CancellationToken cancellationToken = default)
    {
        var request = await BuildRequestAsync(workspace, cancellationToken).ConfigureAwait(false);
        await client.ConnectAsync(request, cancellationToken).ConfigureAwait(false);
        _ = await client.FileExistsAsync(request.RemoteRoot, cancellationToken).ConfigureAwait(false);
    }
}
