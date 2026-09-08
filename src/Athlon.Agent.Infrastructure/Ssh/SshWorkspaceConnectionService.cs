using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure.Ssh;

/// <summary>
/// Coordinates per-root-session SSH connections for the app shell. Synchronizing a session
/// only ensures that session's own connection slot, so switching between sessions never tears
/// down a connection another session (for example a background turn) is still using.
/// </summary>
public sealed class SshWorkspaceConnectionService(
    ISshConnectionRegistry registry,
    ICredentialStore credentialStore,
    IAppLogger logger)
{
    private readonly IAppLogger _logger = logger.ForContext("SshWorkspaceConnectionService");

    /// <summary>
    /// Ensures the given session holds the connection its workspace configuration requires.
    /// Non-SSH sessions only release their own slot; other sessions are untouched.
    /// </summary>
    public async Task SyncAsync(AgentSession session, AppSettings settings, CancellationToken cancellationToken = default)
    {
        var match = WorkspaceSessionResolver.FindMatch(session, settings);
        if (match is null || match.WorkspaceKind != WorkspaceKind.Ssh || match.Ssh is null)
        {
            await registry.DisconnectAsync(session.Id, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            var request = await BuildRequestAsync(match, cancellationToken).ConfigureAwait(false);
            await registry.EnsureConnectedAsync(session.Id, request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning("Failed to connect SSH workspace id={WorkspaceId}: {Message}", match.Id, ex.Message);
            throw;
        }
    }

    /// <summary>Points non-turn (UI) SSH callers at the currently displayed session.</summary>
    public void SetDefaultSession(string? sessionId) => registry.DefaultSessionId = sessionId;

    /// <summary>Releases only the given session's connection slot.</summary>
    public Task DisconnectSessionAsync(string sessionId, CancellationToken cancellationToken = default) =>
        registry.DisconnectAsync(sessionId, cancellationToken);

    /// <summary>Releases every session connection slot (application shutdown).</summary>
    public Task DisconnectAllAsync(CancellationToken cancellationToken = default) =>
        registry.DisconnectAllAsync(cancellationToken);

    /// <summary>
    /// Creates an isolated scratch connection for one-off flows (e.g. the SSH connect wizard
    /// browsing a remote tree). The probe is not part of any session and never touches the
    /// session pool; dispose it when the flow completes.
    /// </summary>
    public SshWorkspaceClient CreateProbeClient()
    {
        var probe = new SshWorkspaceClient(logger)
        {
            DefaultSessionId = "ssh-probe:" + Guid.NewGuid().ToString("N")
        };
        return probe;
    }

    /// <summary>Connects a probe (see <see cref="CreateProbeClient"/>) to the given workspace.</summary>
    public async Task ConnectProbeAsync(
        SshWorkspaceClient probe,
        WorkspaceSettings workspace,
        CancellationToken cancellationToken = default)
    {
        var probeId = probe.DefaultSessionId
            ?? throw new InvalidOperationException("Probe client was not initialized.");
        var request = await BuildRequestAsync(workspace, cancellationToken).ConfigureAwait(false);
        await probe.EnsureConnectedAsync(probeId, request, cancellationToken).ConfigureAwait(false);
    }

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

    /// <summary>
    /// Validates credentials against the remote host using a one-shot connection that is
    /// disposed afterwards. Never occupies a session slot.
    /// </summary>
    public async Task TestConnectionAsync(WorkspaceSettings workspace, CancellationToken cancellationToken = default)
    {
        var request = await BuildRequestAsync(workspace, cancellationToken).ConfigureAwait(false);
        var (ssh, sftp) = SshConnectionFactory.CreateClients(request);
        try
        {
            await Task.Run(
                () => SshConnectionFactory.ConnectPair(ssh, sftp, cancellationToken),
                cancellationToken).ConfigureAwait(false);

            // Exercise the connection once (single round-trip) so auth/sftp failures surface.
            await Task.Run(
                () => sftp.GetAttributes(request.RemoteRoot),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            SshConnectionFactory.SafeDispose(ssh, sftp);
        }
    }
}
