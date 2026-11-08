using Athlon.Agent.App.Services;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Teams;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Infrastructure.Storage;
using Athlon.Agent.Infrastructure.Teams;

namespace Athlon.Agent.Tests.Teams;

public sealed class TeamChatArtifactsTests : IDisposable
{
    private readonly string _root;
    private readonly TestPathProvider _paths;
    private readonly JsonFileStore _json;
    private readonly FileTeamTaskStore _tasks;

    public TeamChatArtifactsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "athlon-team-chat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _paths = new TestPathProvider(_root);
        _paths.EnsureCreated();
        _json = new JsonFileStore();
        _tasks = new FileTeamTaskStore(_paths, _json);
        TeamChatArtifacts.ConfigureTaskStore(_tasks);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    [Fact]
    public void TryParseTeamTaskIdFromContent_ParsesQueryAndPathForms()
    {
        Assert.True(TeamChatArtifacts.TryParseTeamTaskIdFromContent(
            "done [open](athlon://team-trace?taskId=tm_abc123)",
            out var a));
        Assert.Equal("tm_abc123", a);

        Assert.True(TeamChatArtifacts.TryParseTeamTaskIdFromContent(
            "see athlon://team-trace/tm_xyz",
            out var b));
        Assert.Equal("tm_xyz", b);
    }

    [Fact]
    public void FormatStepMarkdown_IncludesFullBody()
    {
        var md = TeamChatArtifacts.FormatStepMarkdown(new TeamRunProgress
        {
            Kind = TeamRunProgressKind.WorkerCompleted,
            RootSessionId = "s",
            TaskId = "t",
            Title = "Recipe Runner",
            StatusLabel = "Succeeded",
            Iteration = 2,
            ResponseText = "## Result\nstatus: completed\nfull body here"
        });

        Assert.Contains("### Teams · Recipe Runner · Succeeded · iter 2", md, StringComparison.Ordinal);
        Assert.Contains("full body here", md, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CollectTaskFiles_IncludesApplyPatchPathsFromUpdateMarkers()
    {
        var task = new TeamTask
        {
            RootSessionId = "sess-patch",
            TaskId = "tm_patch1",
            TeamId = "t1",
            Goal = "patch",
            Status = TeamTaskStatus.Succeeded,
            SubTasks =
            [
                new TeamSubTask
                {
                    Id = "sub_1",
                    Title = "Runner",
                    AssigneeProfileId = "worker",
                    Status = TeamSubTaskStatus.Succeeded,
                    MemberSessionId = "mbr_patch",
                    Iteration = 1
                }
            ]
        };
        await _tasks.SaveAsync(task);

        var memberDir = _tasks.ResolveMemberDirectory(task.RootSessionId, task.TaskId, "mbr_patch");
        Directory.CreateDirectory(memberDir);
        var patch = ChatMessage.Create(
            MessageRole.Tool,
            string.Join(
                Environment.NewLine,
                "ToolCallId: call-p",
                "Tool `apply_patch` succeeded.",
                "",
                "Arguments: patch=*** Begin Patch",
                "*** Update File: src/Athlon.Agent.App/ViewModels/MainShellViewModel.cs",
                "@@",
                "+    private void OnChatPagePropertyChanged()",
                "*** End Patch",
                "Summary: Patched 1 file(s)",
                "",
                "src/Athlon.Agent.App/ViewModels/MainShellViewModel.cs"));
        var session = AgentSession.Create("mbr_patch").WithMessages([patch]);
        await _json.SaveAsync(Path.Combine(memberDir, "session.json"), session);

        var files = TeamChatArtifacts.CollectTaskFiles(task, _tasks);
        Assert.Contains(
            files,
            f => f.RelativePath.Replace('\\', '/')
                .Contains("MainShellViewModel.cs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CollectTaskFiles_MergesMemberFileEdits()
    {
        var task = new TeamTask
        {
            RootSessionId = "sess1",
            TaskId = "tm_files1",
            TeamId = "t1",
            Goal = "edit",
            Status = TeamTaskStatus.Succeeded,
            LeaderSessionId = "ldr_1",
            SubTasks =
            [
                new TeamSubTask
                {
                    Id = "sub_1",
                    Title = "Runner",
                    AssigneeProfileId = "worker",
                    Status = TeamSubTaskStatus.Succeeded,
                    MemberSessionId = "mbr_1",
                    Iteration = 1
                }
            ]
        };
        await _tasks.SaveAsync(task);

        var memberDir = _tasks.ResolveMemberDirectory(task.RootSessionId, task.TaskId, "mbr_1");
        Directory.CreateDirectory(memberDir);
        var edit = ChatMessage.Create(
            MessageRole.Tool,
            string.Join(
                Environment.NewLine,
                "ToolCallId: call-1",
                "Tool `file_edit` succeeded.",
                "",
                "Arguments: path = src/Foo.cs",
                "Summary: Edited",
                "",
                "--- a/src/Foo.cs",
                "+++ b/src/Foo.cs",
                "@@ -1,1 +1,1 @@",
                "-old",
                "+new"));
        var session = AgentSession.Create("mbr_1").WithMessages([edit]);
        await _json.SaveAsync(Path.Combine(memberDir, "session.json"), session);

        var files = TeamChatArtifacts.CollectTaskFiles(task, _tasks);
        Assert.Contains(files, f => f.RelativePath.Replace('\\', '/').Contains("src/Foo.cs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BuildReplayEvents_RebuildsFilesChanged_FromTeamTraceLink()
    {
        var task = new TeamTask
        {
            RootSessionId = "sess-replay",
            TaskId = "tm_replay1",
            TeamId = "t1",
            Goal = "edit",
            Status = TeamTaskStatus.Succeeded,
            SubTasks =
            [
                new TeamSubTask
                {
                    Id = "sub_1",
                    Title = "Runner",
                    AssigneeProfileId = "worker",
                    Status = TeamSubTaskStatus.Succeeded,
                    MemberSessionId = "mbr_replay",
                    Iteration = 1
                }
            ]
        };
        await _tasks.SaveAsync(task);

        var memberDir = _tasks.ResolveMemberDirectory(task.RootSessionId, task.TaskId, "mbr_replay");
        Directory.CreateDirectory(memberDir);
        var edit = ChatMessage.Create(
            MessageRole.Tool,
            string.Join(
                Environment.NewLine,
                "ToolCallId: call-r",
                "Tool `file_edit` succeeded.",
                "",
                "Arguments: path = Bar.cs",
                "Summary: Edited",
                "",
                "--- a/Bar.cs",
                "+++ b/Bar.cs",
                "@@ -1,1 +1,1 @@",
                "-a",
                "+b"));
        var session = AgentSession.Create("mbr_replay").WithMessages([edit]);
        await _json.SaveAsync(Path.Combine(memberDir, "session.json"), session);

        var user = ChatMessage.Create(MessageRole.User, "optimize");
        var assistant = ChatMessage.Create(
            MessageRole.Assistant,
            "Done. [Trace](athlon://team-trace?taskId=tm_replay1)");

        var events = ChatEventSerializer.BuildReplayEvents(
            [new ChatMessageViewModel(user), new ChatMessageViewModel(assistant)],
            showToolCalls: false,
            rootSessionId: task.RootSessionId);

        Assert.Contains(events, json => json.Contains("FILES_CHANGED", StringComparison.Ordinal));
    }

    private sealed class TestPathProvider(string root) : IAppPathProvider
    {
        public string RootPath { get; } = root;
        public string ConfigPath => Path.Combine(RootPath, "config");
        public string SessionsPath => Path.Combine(RootPath, "sessions");
        public string AuditPath => Path.Combine(RootPath, "audit");
        public string LogsPath => Path.Combine(RootPath, "logs");
        public string CredentialsPath => Path.Combine(RootPath, "credentials");
        public string SkillsPath => Path.Combine(RootPath, "skills");

        public void EnsureCreated()
        {
            Directory.CreateDirectory(ConfigPath);
            Directory.CreateDirectory(SessionsPath);
            Directory.CreateDirectory(Path.Combine(ConfigPath, "teams"));
            Directory.CreateDirectory(Path.Combine(ConfigPath, "agent-profiles"));
        }

        public string ResolveSkillPath(string path) => path;
    }
}
