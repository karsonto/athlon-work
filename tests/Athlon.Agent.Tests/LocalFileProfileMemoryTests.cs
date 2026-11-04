using Athlon.Agent.Core;
using Athlon.Agent.Core.Memory;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Infrastructure.Memory;

namespace Athlon.Agent.Tests;

public sealed class LocalFileProfileMemoryTests
{
    [Fact]
    public async Task Read_returns_null_when_document_never_written()
    {
        using var temp = new TempDirectoryScope("athlon-profile-read");
        var memory = CreateSut(temp.Root);

        var document = await memory.ReadAsync(ProfileKind.User);

        Assert.Null(document);
        Assert.False(Directory.Exists(Path.Combine(temp.Root, "profile")));
    }

    [Fact]
    public async Task Write_then_read_round_trips()
    {
        using var temp = new TempDirectoryScope("athlon-profile-roundtrip");
        var memory = CreateSut(temp.Root);

        await memory.WriteAsync(ProfileKind.User, "# USER\n- prefers Chinese");
        var document = await memory.ReadAsync(ProfileKind.User);

        Assert.NotNull(document);
        Assert.Equal("# USER\n- prefers Chinese", document!.Content);
        Assert.NotNull(document.UpdatedAt);
        Assert.Equal(ProfileBackendKind.Local, document.Backend);
        Assert.True(File.Exists(Path.Combine(temp.Root, "profile", "USER.md")));
    }

    [Fact]
    public async Task Directory_is_created_only_on_write()
    {
        using var temp = new TempDirectoryScope("athlon-profile-lazydir");
        var memory = CreateSut(temp.Root);

        await memory.ReadAsync(ProfileKind.Lessons);
        Assert.False(Directory.Exists(Path.Combine(temp.Root, "profile")));

        await memory.WriteAsync(ProfileKind.Lessons, "# LESSONS\n- a");
        Assert.True(Directory.Exists(Path.Combine(temp.Root, "profile")));
    }

    [Fact]
    public async Task Concurrent_writes_leave_a_parseable_document()
    {
        using var temp = new TempDirectoryScope("athlon-profile-concurrent");
        var memory = CreateSut(temp.Root);
        var payload = string.Join("\n", Enumerable.Range(0, 200).Select(i => $"- entry {i}"));

        await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => memory.WriteAsync(ProfileKind.User, payload))));

        var content = await File.ReadAllTextAsync(Path.Combine(temp.Root, "profile", "USER.md"));
        Assert.Equal(payload, content.TrimEnd());
    }

    [Fact]
    public async Task Truncation_keeps_the_title_and_whole_sections()
    {
        using var temp = new TempDirectoryScope("athlon-profile-truncate");
        var settings = new AppSettings { Profile = { UserMaxChars = 220 } };
        var memory = CreateSut(temp.Root, settings);
        var content = "# USER\n\n## Style\n" + string.Join("\n", Enumerable.Repeat("- uses Chinese", 20))
            + "\n\n## Tools\n- prefers dotnet";

        await memory.WriteAsync(ProfileKind.User, content);
        var stored = await File.ReadAllTextAsync(Path.Combine(temp.Root, "profile", "USER.md"));

        Assert.StartsWith("# USER", stored, StringComparison.Ordinal);
        Assert.EndsWith("...(truncated)", stored, StringComparison.Ordinal);
        Assert.True(stored.Length <= settings.Profile.UserMaxChars, $"length {stored.Length}");
    }

    [Fact]
    public async Task Delete_reports_whether_the_document_existed()
    {
        using var temp = new TempDirectoryScope("athlon-profile-delete");
        var memory = CreateSut(temp.Root);

        Assert.False(await memory.DeleteAsync(ProfileKind.User));

        await memory.WriteAsync(ProfileKind.User, "# USER");
        Assert.True(await memory.DeleteAsync(ProfileKind.User));
        Assert.Null(await memory.ReadAsync(ProfileKind.User));
    }

    [Fact]
    public void Capabilities_are_writable_and_deletable_local()
    {
        using var temp = new TempDirectoryScope("athlon-profile-capabilities");
        var memory = CreateSut(temp.Root);

        Assert.True(memory.Capabilities.CanWrite);
        Assert.True(memory.Capabilities.CanDelete);
        Assert.False(memory.Capabilities.IsRemote);
        Assert.Equal(ProfileBackendKind.Local, memory.Capabilities.Backend);
    }

    private static LocalFileProfileMemory CreateSut(string root, AppSettings? settings = null) =>
        new(new TestAppPathProvider(root), settings ?? new AppSettings(), new NoOpLogger());

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
