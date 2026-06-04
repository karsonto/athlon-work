using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class ExecuteCommandToolTests
{
    [Fact]
    public async Task InvokeAsync_SuccessfulCommand_ReturnsStdout()
    {
        var tool = CreateTool();
        var result = await tool.InvokeAsync(new ToolInvocation(
            "execute_command",
            new Dictionary<string, string> { ["command"] = "echo hello-athlon" }));

        Assert.True(result.Succeeded, result.Error ?? result.Summary);
        Assert.Contains("hello-athlon", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvokeAsync_TimesOut_ReturnsFailureWithoutThrowing()
    {
        var tool = CreateTool();
        var result = await tool.InvokeAsync(new ToolInvocation(
            "execute_command",
            new Dictionary<string, string>
            {
                ["command"] = "timeout /t 3 /nobreak >nul",
                ["timeout"] = "1"
            }));

        Assert.False(result.Succeeded);
        Assert.Contains("timed out", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvokeAsync_UserCancel_ThrowsOperationCanceledException()
    {
        var tool = CreateTool();
        using var cts = new CancellationTokenSource();
        var invokeTask = tool.InvokeAsync(
            new ToolInvocation(
                "execute_command",
                new Dictionary<string, string>
                {
                    ["command"] = "timeout /t 30 /nobreak >nul",
                    ["timeout"] = "60"
                }),
            cts.Token);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invokeTask);
    }

    [Fact]
    public async Task InvokeAsync_InvalidWorkingDirectory_ReturnsFailureWithoutThrowing()
    {
        var tool = CreateTool();
        var missingDir = Path.Combine(Path.GetTempPath(), "athlon-missing-cwd-" + Guid.NewGuid().ToString("N"));
        var result = await tool.InvokeAsync(new ToolInvocation(
            "execute_command",
            new Dictionary<string, string>
            {
                ["command"] = "echo hello",
                ["cwd"] = missingDir
            }));

        Assert.False(result.Succeeded);
        Assert.Contains("working directory", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvokeAsync_DeniedCommand_ReturnsFailure()
    {
        var tool = CreateTool();
        var result = await tool.InvokeAsync(new ToolInvocation(
            "execute_command",
            new Dictionary<string, string> { ["command"] = "del /s /q C:\\temp" }));

        Assert.False(result.Succeeded);
        Assert.Equal("Command denied", result.Summary);
    }

    [Fact]
    public void DefaultAndMaxTimeout_AreOneHour()
    {
        Assert.Equal(3600, ExecuteCommandTool.DefaultTimeoutSeconds);
        Assert.Equal(3600, ExecuteCommandTool.MaxTimeoutSeconds);
    }

    [Fact]
    public async Task InvokeAsync_WithWorkspace_DefaultsCwdToWorkspaceRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"athlon-exec-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var tool = CreateTool(root);
            var result = await tool.InvokeAsync(new ToolInvocation(
                "execute_command",
                new Dictionary<string, string> { ["command"] = "cd" }));

            Assert.True(result.Succeeded, result.Error ?? result.Summary);
            Assert.Contains(Path.GetFileName(root), result.Content, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InvokeAsync_WithWorkspace_ResolvesRelativeCwd()
    {
        var root = Path.Combine(Path.GetTempPath(), $"athlon-exec-sub-{Guid.NewGuid():N}");
        var subDir = Path.Combine(root, "docs", "中文目录");
        Directory.CreateDirectory(subDir);
        await File.WriteAllTextAsync(Path.Combine(subDir, "marker.txt"), "ok");
        try
        {
            var tool = CreateTool(root);
            var result = await tool.InvokeAsync(new ToolInvocation(
                "execute_command",
                new Dictionary<string, string>
                {
                    ["command"] = "type marker.txt",
                    ["cwd"] = @"docs\中文目录"
                }));

            Assert.True(result.Succeeded, result.Error ?? result.Content);
            Assert.Contains("ok", result.Content, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InvokeAsync_Utf8Console_ReturnsChineseStdout()
    {
        var tool = CreateTool();
        var result = await tool.InvokeAsync(new ToolInvocation(
            "execute_command",
            new Dictionary<string, string> { ["command"] = "echo 你好Athlon" }));

        Assert.True(result.Succeeded, result.Error ?? result.Content);
        Assert.Contains("你好Athlon", result.Content, StringComparison.Ordinal);
    }

    private static ExecuteCommandTool CreateTool(string? workspaceRoot = null)
    {
        var paths = new AppPathProvider();
        paths.EnsureCreated();
        var logger = AppLogger.Create(new LoggingSettings(), paths.LogsPath);
        var audit = new AuditLogService(logger, paths, new JsonFileStore());
        var context = new ActiveWorkspaceContext();
        if (!string.IsNullOrWhiteSpace(workspaceRoot))
        {
            context.SetWorkspace(workspaceRoot);
        }

        var guard = new WorkspaceGuard(context, new AppSettings(), paths);
        return new ExecuteCommandTool(new AppSettings(), guard, audit, new ExecuteCommandProcessRegistry());
    }
}
