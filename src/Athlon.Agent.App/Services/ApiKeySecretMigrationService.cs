using Athlon.Agent.Core;

namespace Athlon.Agent.App.Services;

public sealed class ApiKeySecretMigrationService(ICredentialStore credentialStore)
{
    public async Task<bool> EnsureCurrentApiKeySecretAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var hasCurrentSecret = await credentialStore
            .HasSecretAsync(ModelSettings.ApiKeySecretName, cancellationToken)
            .ConfigureAwait(false);
        if (hasCurrentSecret || string.IsNullOrWhiteSpace(settings.Model.LegacyApiKeyCredentialName))
        {
            return hasCurrentSecret;
        }

        var legacySecret = await credentialStore
            .GetSecretAsync(settings.Model.LegacyApiKeyCredentialName, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(legacySecret))
        {
            return false;
        }

        await credentialStore
            .SaveSecretAsync(ModelSettings.ApiKeySecretName, legacySecret, cancellationToken)
            .ConfigureAwait(false);
        return true;
    }
}
