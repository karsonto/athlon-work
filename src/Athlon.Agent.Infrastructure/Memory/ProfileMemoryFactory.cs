using Athlon.Agent.Core;
using Athlon.Agent.Core.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Athlon.Agent.Infrastructure.Memory;

/// <summary>
/// Resolves the profile backend from <see cref="ProfileSettings.Backend"/>. This is the single
/// extension point for a future remote store: callers depend on <see cref="IProfileMemory"/> only.
/// </summary>
public static class ProfileMemoryFactory
{
    public static IProfileMemory Create(IServiceProvider services)
    {
        var settings = services.GetRequiredService<AppSettings>();
        return settings.Profile.Backend switch
        {
            ProfileBackendKind.Local => ActivatorUtilities.CreateInstance<LocalFileProfileMemory>(services),
            // Remote arrives in a later phase; fall back to local so a stale settings file never
            // leaves the app without a profile backend.
            _ => ActivatorUtilities.CreateInstance<LocalFileProfileMemory>(services)
        };
    }
}
