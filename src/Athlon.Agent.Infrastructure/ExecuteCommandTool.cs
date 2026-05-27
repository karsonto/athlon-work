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

public sealed class ExecuteCommandTool(AppSettings settings, AuditLogService audit) : IAgentTool
{
    public ToolDefinition Definition { get; } = new("execute_command", "Execute a command after explicit user approval.", new Dictionary<string, string> { ["command"] = "Command line", ["cwd"] = "Working directory", ["timeout"] = "Timeout seconds" }, RequiresApproval: true);

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!ToolArguments.TryGetRequired(invocation, "command", out var command, out var error)) return error;
        if (settings.ToolPermissions.CommandDenyList.Any(deny => command.Contains(deny, StringComparison.OrdinalIgnoreCase))) return ToolResult.Failure("Command denied", command);

        var cwd = invocation.Arguments.GetValueOrDefault("cwd") ?? Environment.CurrentDirectory;
        var timeout = ToolArguments.GetInt32(invocation, "timeout", 60);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeout));

        var startInfo = new ProcessStartInfo("cmd.exe", "/c " + command) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        var sw = Stopwatch.StartNew();
        using var process = Process.Start(startInfo)!;
        var stdout = await process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderr = await process.StandardError.ReadToEndAsync(cts.Token);
        await process.WaitForExitAsync(cts.Token);
        sw.Stop();
        var content = string.IsNullOrWhiteSpace(stderr) ? stdout : stdout + Environment.NewLine + stderr;
        await audit.WriteAsync("execute_command", new { command, cwd, process.ExitCode, elapsedMs = sw.ElapsedMilliseconds }, cancellationToken);
        return process.ExitCode == 0 ? ToolResult.Success($"Command exited 0 in {sw.ElapsedMilliseconds}ms", content, sw.Elapsed) : ToolResult.Failure("Command failed", content, sw.Elapsed);
    }
}
