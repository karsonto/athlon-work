using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Athlon.Agent.Infrastructure;

public sealed class DpapiCredentialStore(IAppPathProvider paths) : ICredentialStore
{
    public Task SaveSecretAsync(string name, string secret, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(paths.CredentialsPath);
        var plainBytes = Encoding.UTF8.GetBytes(secret);
        var protectedBytes = ProtectedData.Protect(plainBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        File.WriteAllText(GetCredentialPath(name), Convert.ToBase64String(protectedBytes));
        return Task.CompletedTask;
    }

    public Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken = default)
    {
        var path = GetCredentialPath(name);
        if (!File.Exists(path))
        {
            return Task.FromResult<string?>(null);
        }

        var encoded = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(encoded))
        {
            return Task.FromResult<string?>(null);
        }

        var protectedBytes = Convert.FromBase64String(encoded);
        var plainBytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        return Task.FromResult<string?>(Encoding.UTF8.GetString(plainBytes));
    }

    public Task<bool> HasSecretAsync(string name, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(File.Exists(GetCredentialPath(name)));
    }

    private string GetCredentialPath(string name)
    {
        var safeName = FileNameSanitizer.Sanitize(string.IsNullOrWhiteSpace(name) ? "default" : name);
        return Path.Combine(paths.CredentialsPath, $"{safeName}.secret");
    }
}
