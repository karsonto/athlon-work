using Athlon.Agent.App.Localization;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Infrastructure;
using System.Windows;

namespace Athlon.Agent.Tests;

public sealed class FileEditorSessionPlanTests
{
    private static readonly ILocalizationService Localization = new LocalizationService();

    [Fact]
    public void OpenOrUpdateSessionPlan_CreatesSingleTab_AndUpdatesInPlace()
    {
        var editor = CreateEditor();
        var plan = new SessionPlan
        {
            Title = "Demo Plan",
            Overview = "First overview",
            Body = "## Steps\n\n- one",
            Status = SessionPlanStatuses.AwaitingConfirmation,
            UpdatedAt = "2026-01-01T00:00:00Z",
            Todos = [new SessionPlanTodoItem { Id = "step-1", Content = "Do one" }]
        };

        editor.OpenOrUpdateSessionPlan(plan, "session-a", activateTab: true);

        Assert.Single(editor.Tabs);
        Assert.True(editor.ActiveDocument!.IsSessionPlan);
        Assert.True(editor.ShowPlanBuildButton);
        Assert.Contains("Demo", editor.ActiveDocument.DisplayName, StringComparison.Ordinal);
        Assert.Contains("First overview", editor.ActiveDocument.Content, StringComparison.Ordinal);

        plan.Overview = "Revised overview";
        plan.Body = "## Steps\n\n- one\n- two";
        plan.UpdatedAt = "2026-01-01T01:00:00Z";
        editor.OpenOrUpdateSessionPlan(plan, "session-a", activateTab: false);

        Assert.Single(editor.Tabs);
        Assert.Contains("Revised overview", editor.ActiveDocument!.Content, StringComparison.Ordinal);
        Assert.True(editor.ShowPlanBuildButton);
    }

    [Fact]
    public void CloseSessionPlanTab_RemovesPlanTab()
    {
        var editor = CreateEditor();
        var plan = new SessionPlan
        {
            Title = "Close Me",
            Overview = "Overview",
            Body = "Body",
            Status = SessionPlanStatuses.AwaitingConfirmation,
            Todos = [new SessionPlanTodoItem { Id = "a", Content = "A" }]
        };

        editor.OpenOrUpdateSessionPlan(plan, "session-b", activateTab: true);
        editor.CloseSessionPlanTab("session-b");

        Assert.Empty(editor.Tabs);
        Assert.False(editor.ShowPlanBuildButton);
    }

    [Fact]
    public void ShowPlanBuildButton_IsFalse_WhenApproved()
    {
        var editor = CreateEditor();
        var plan = new SessionPlan
        {
            Title = "Approved",
            Overview = "Overview",
            Body = "Body",
            Status = SessionPlanStatuses.AwaitingConfirmation,
            Todos = [new SessionPlanTodoItem { Id = "a", Content = "A" }]
        };

        editor.OpenOrUpdateSessionPlan(plan, "session-c", activateTab: true);
        Assert.True(editor.ShowPlanBuildButton);

        plan.Status = SessionPlanStatuses.Approved;
        editor.OpenOrUpdateSessionPlan(plan, "session-c", activateTab: false);

        Assert.False(editor.ActiveDocument!.CanBuild);
        Assert.False(editor.ShowPlanBuildButton);
    }

    private static FileEditorViewModel CreateEditor()
    {
        var root = Path.Combine(Path.GetTempPath(), $"athlon-plan-editor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var context = new ActiveWorkspaceContext();
        context.SetWorkspace(root);
        var appData = Path.Combine(root, ".athlon-agent");
        Directory.CreateDirectory(appData);
        var guard = new WorkspaceGuard(
            context,
            new AgentRunContextAccessor(),
            new AppSettings(),
            new TestPathProvider(appData));
        var service = new WorkspaceFileEditorService(guard, new AppSettings(), new DisconnectedSshClient());
        return new FileEditorViewModel(service, guard, Localization, new NoOpUserNotifier());
    }

    private sealed class TestPathProvider(string rootPath) : IAppPathProvider
    {
        public string RootPath { get; } = rootPath;
        public string ConfigPath => Path.Combine(rootPath, "config");
        public string SessionsPath => Path.Combine(rootPath, "sessions");
        public string AuditPath => Path.Combine(rootPath, "audit");
        public string LogsPath => Path.Combine(rootPath, "logs");
        public string CredentialsPath => Path.Combine(rootPath, "credentials");
        public string SkillsPath => Path.Combine(rootPath, "skills");
        public void EnsureCreated() => Directory.CreateDirectory(rootPath);
        public string ResolveSkillPath(string path) =>
            Path.IsPathRooted(path) ? path : Path.Combine(SkillsPath, path);
    }

    private sealed class DisconnectedSshClient : ISshWorkspaceClient
    {
        public bool IsConnected => false;
        public string? RemoteRoot => null;
        public string? ConnectedWorkspaceId => null;

        public Task ConnectAsync(SshConnectRequest request, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> FileExistsAsync(string remotePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<SshFileInfo> GetFileInfoAsync(string remotePath, CancellationToken cancellationToken = default) =>
            throw new FileNotFoundException(remotePath);

        public Task<SshFileInfo?> TryGetFileInfoAsync(string remotePath, CancellationToken cancellationToken = default) =>
            Task.FromResult<SshFileInfo?>(null);

        public Task<string> ReadTextAsync(string remotePath, CancellationToken cancellationToken = default) =>
            throw new FileNotFoundException(remotePath);

        public Task<T> ReadViaStreamAsync<T>(
            string remotePath,
            Func<Stream, CancellationToken, Task<T>> reader,
            CancellationToken cancellationToken = default) =>
            throw new FileNotFoundException(remotePath);

        public Task WriteTextAsync(string remotePath, string content, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DownloadFileAsync(string remotePath, string localPath, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task UploadFileAsync(string localPath, string remotePath, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task CreateDirectoryAsync(string remotePath, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public async IAsyncEnumerable<SshEntry> ListAsync(
            string remotePath,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            yield break;
        }

        public Task<SshCommandResult> ExecuteAsync(
            string command,
            string? workingDirectory,
            TimeSpan timeout,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SshCommandResult(1, "", "disconnected", TimeSpan.Zero));

        public Task<bool> HasCommandAsync(string commandName, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class NoOpUserNotifier : IUserNotifier
    {
        public void Info(string titleKey, string messageKey, params object[] messageArgs) { }

        public void Warning(string titleKey, string messageKey, params object[] messageArgs) { }

        public void InfoText(string titleKey, string messageText) { }

        public void WarningText(string titleKey, string messageText) { }

        public bool Confirm(string titleKey, string messageKey, params object[] messageArgs) => true;

        public bool ConfirmYesNo(string titleKey, string messageKey, params object[] messageArgs) => true;

        public MessageBoxResult AskYesNoCancel(string titleKey, string messageKey, params object[] messageArgs) =>
            MessageBoxResult.Yes;
    }
}
