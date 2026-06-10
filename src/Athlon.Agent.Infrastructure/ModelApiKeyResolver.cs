using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure;

internal static class ModelApiKeyResolver
{
    public static async Task<string> ResolveAsync(
        ICredentialStore credentialStore,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        var apiKey = await credentialStore.GetSecretAsync(ModelSettings.ApiKeySecretName, cancellationToken);
        if (string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(settings.Model.LegacyApiKeyCredentialName))
        {
            apiKey = await credentialStore.GetSecretAsync(settings.Model.LegacyApiKeyCredentialName, cancellationToken);
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                await credentialStore.SaveSecretAsync(ModelSettings.ApiKeySecretName, apiKey, cancellationToken);
            }
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("模型 API Key 未配置。请在 Settings > Model 中输入 API Key 并保存，或设置环境变量 OPENAI_API_KEY。");
        }

        return apiKey;
    }
}
