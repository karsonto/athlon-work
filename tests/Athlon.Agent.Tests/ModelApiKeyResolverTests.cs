using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class ModelApiKeyResolverTests
{
    [Fact]
    public async Task ResolveAsync_ReturnsNull_WhenNoCredentialOrEnvironmentVariable()
    {
        var previous = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
        try
        {
            var result = await ModelApiKeyResolver.ResolveAsync(
                new EmptyCredentialStore(),
                new AppSettings());

            Assert.Null(result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", previous);
        }
    }

    [Fact]
    public async Task ResolveAsync_ReturnsStoredCredential_WhenPresent()
    {
        var credentials = new DictionaryCredentialStore();
        await credentials.SaveSecretAsync(ModelSettings.ApiKeySecretName, "sk-stored");

        var result = await ModelApiKeyResolver.ResolveAsync(credentials, new AppSettings());

        Assert.Equal("sk-stored", result);
    }

    [Fact]
    public async Task ResolveAsync_UsesEnvironmentVariable_WhenCredentialMissing()
    {
        var previous = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "sk-env");
        try
        {
            var result = await ModelApiKeyResolver.ResolveAsync(
                new EmptyCredentialStore(),
                new AppSettings());

            Assert.Equal("sk-env", result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", previous);
        }
    }

    private sealed class EmptyCredentialStore : ICredentialStore
    {
        public Task SaveSecretAsync(string name, string secret, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task<bool> HasSecretAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task DeleteSecretAsync(string name, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class DictionaryCredentialStore : ICredentialStore
    {
        private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal);

        public Task SaveSecretAsync(string name, string secret, CancellationToken cancellationToken = default)
        {
            _secrets[name] = secret;
            return Task.CompletedTask;
        }

        public Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult(_secrets.TryGetValue(name, out var value) ? value : null);

        public Task<bool> HasSecretAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult(_secrets.ContainsKey(name));

        public Task DeleteSecretAsync(string name, CancellationToken cancellationToken = default)
        {
            _secrets.Remove(name);
            return Task.CompletedTask;
        }
    }
}
