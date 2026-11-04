using Athlon.Agent.Core;
using Athlon.Agent.Core.Memory;
using Athlon.Agent.Infrastructure.Memory;

namespace Athlon.Agent.Tests;

public sealed class ProfileGetToolTests
{
    [Fact]
    public async Task No_arguments_lists_both_documents()
    {
        var profile = new StubProfileMemory()
            .Seed(ProfileKind.User, "# USER\n- prefers Chinese", DateTimeOffset.UtcNow);
        var tool = CreateSut(profile);

        var result = await tool.InvokeAsync(Invoke());

        Assert.True(result.Succeeded);
        Assert.Contains("user", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("lessons", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("empty", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task File_argument_returns_full_document()
    {
        var profile = new StubProfileMemory()
            .Seed(ProfileKind.Lessons, "# LESSONS\n- check permissions first", DateTimeOffset.UtcNow);
        var tool = CreateSut(profile);

        var result = await tool.InvokeAsync(Invoke(("file", "lessons")));

        Assert.True(result.Succeeded);
        Assert.Equal("# LESSONS\n- check permissions first", result.Content);
    }

    [Fact]
    public async Task Missing_document_reports_empty_without_failing()
    {
        var tool = CreateSut(new StubProfileMemory());

        var result = await tool.InvokeAsync(Invoke(("file", "user")));

        Assert.True(result.Succeeded);
        Assert.Contains("not been written", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_file_returns_failure()
    {
        var tool = CreateSut(new StubProfileMemory());

        var result = await tool.InvokeAsync(Invoke(("file", "skills")));

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Disabled_profile_short_circuits()
    {
        var profile = new StubProfileMemory()
            .Seed(ProfileKind.User, "# USER", DateTimeOffset.UtcNow);
        var tool = CreateSut(profile, new AppSettings { Profile = { Enabled = false } });

        var result = await tool.InvokeAsync(Invoke());

        Assert.True(result.Succeeded);
        Assert.Contains("disabled", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, profile.ReadCount);
    }

    private static ProfileGetTool CreateSut(IProfileMemory profile, AppSettings? settings = null) =>
        new(profile, settings ?? new AppSettings(), new NoOpLogger());

    private static ToolInvocation Invoke(params (string Key, string Value)[] arguments) =>
        new("profile_get", arguments.ToDictionary(pair => pair.Key, pair => pair.Value));

    private sealed class StubProfileMemory : IProfileMemory
    {
        private readonly Dictionary<ProfileKind, ProfileDocument> _documents = new();

        public int ReadCount { get; private set; }

        public ProfileCapabilities Capabilities { get; } =
            new(ProfileBackendKind.Local, CanWrite: true, CanDelete: true, IsRemote: false);

        public StubProfileMemory Seed(ProfileKind kind, string content, DateTimeOffset updatedAt)
        {
            _documents[kind] = new ProfileDocument(kind, content, updatedAt, ProfileBackendKind.Local);
            return this;
        }

        public Task<ProfileDocument?> ReadAsync(ProfileKind kind, CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return Task.FromResult(_documents.TryGetValue(kind, out var document) ? document : null);
        }

        public Task WriteAsync(ProfileKind kind, string content, CancellationToken cancellationToken = default)
        {
            _documents[kind] = new ProfileDocument(kind, content, DateTimeOffset.UtcNow, ProfileBackendKind.Local);
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(ProfileKind kind, CancellationToken cancellationToken = default) =>
            Task.FromResult(_documents.Remove(kind));
    }
}
