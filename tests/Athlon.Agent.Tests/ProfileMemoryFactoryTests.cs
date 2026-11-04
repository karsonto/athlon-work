using Athlon.Agent.Core;
using Athlon.Agent.Core.Memory;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Infrastructure.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Athlon.Agent.Tests;

public sealed class ProfileMemoryFactoryTests
{
    [Fact]
    public void Local_backend_resolves_to_the_file_implementation()
    {
        using var temp = new TempDirectoryScope("athlon-profile-factory");
        using var provider = BuildProvider(temp.Root, new AppSettings());

        var profile = provider.GetRequiredService<IProfileMemory>();

        Assert.IsType<LocalFileProfileMemory>(profile);
        Assert.Equal(ProfileBackendKind.Local, profile.Capabilities.Backend);
    }

    [Fact]
    public void Remote_backend_falls_back_to_local_instead_of_throwing()
    {
        using var temp = new TempDirectoryScope("athlon-profile-factory-remote");
        var settings = new AppSettings { Profile = { Backend = ProfileBackendKind.Remote } };
        using var provider = BuildProvider(temp.Root, settings);

        var profile = provider.GetRequiredService<IProfileMemory>();

        Assert.IsType<LocalFileProfileMemory>(profile);
    }

    private static ServiceProvider BuildProvider(string root, AppSettings settings)
    {
        var services = new ServiceCollection();
        services.AddSingleton(settings);
        services.AddSingleton<IAppPathProvider>(new TestAppPathProvider(root));
        services.AddSingleton<IAppLogger>(new NoOpLogger());
        services.AddSingleton<IProfileMemory>(ProfileMemoryFactory.Create);
        return services.BuildServiceProvider();
    }

    private sealed class TestAppPathProvider(string root) : IAppPathProvider
    {
        public string RootPath { get; } = root;
        public string ConfigPath => Path.Combine(RootPath, "config");
        public string SessionsPath => Path.Combine(RootPath, "sessions");
        public string AuditPath => Path.Combine(RootPath, "audit");
        public string LogsPath => Path.Combine(RootPath, "logs");
        public string CredentialsPath => Path.Combine(RootPath, "credentials");
        public string SkillsPath => Path.Combine(RootPath, "skills");

        public void EnsureCreated() => Directory.CreateDirectory(RootPath);

        public string ResolveSkillPath(string path) => path;
    }
}
