using System.IO;
using System.Text;
using System.Text.Json;
using Athlon.Agent.Core;

namespace Athlon.Agent.App.Services.Room;

/// <summary>Sandbox <c>browser_navigate</c> via headless Chromium CDP inside the room container.</summary>
internal sealed class RoomDockerBrowserNavigateTool(IRoomSandboxBackend sandbox) : IAgentTool
{
    private const string BrowserCtl = "/opt/athlon/browser_ctl.py";

    public ToolDefinition Definition { get; } = new(
        "browser_navigate",
        "Navigate the sandbox Chromium browser. Pass url to open a page. "
            + "Special url values: \"back\", \"forward\", \"reload\". "
            + "Local workspace files may be opened with file:///workspace/... paths.",
        ToolSchema.Object()
            .String(
                "url",
                "Absolute URL (https://… or file:///…), host name, or back/forward/reload.",
                required: true,
                minLength: 1)
            .Build(),
        RequiresApproval: true);

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        var url = invocation.Arguments.GetString("url")?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(url))
        {
            return ToolResult.Failure("Missing url", "url is required.");
        }

        var roomId = RoomDockerToolFactory.RequireRoomId();
        string command;
        string actionLabel;
        if (string.Equals(url, "back", StringComparison.OrdinalIgnoreCase))
        {
            command = $"python3 {BrowserCtl} back";
            actionLabel = "back";
        }
        else if (string.Equals(url, "forward", StringComparison.OrdinalIgnoreCase))
        {
            command = $"python3 {BrowserCtl} forward";
            actionLabel = "forward";
        }
        else if (string.Equals(url, "reload", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(url, "refresh", StringComparison.OrdinalIgnoreCase))
        {
            command = $"python3 {BrowserCtl} reload";
            actionLabel = "reload";
        }
        else
        {
            var target = NormalizeNavigateUrl(url);
            command = $"python3 {BrowserCtl} navigate {ShellQuote(target)}";
            actionLabel = "navigate";
        }

        RoomSandboxExecResult result;
        try
        {
            result = await sandbox
                .ExecAsync(roomId, command, RoomSandboxPathMap.ContainerWorkspaceRoot, 90, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ToolResult.Failure("Browser navigate failed", ex.Message);
        }

        if (!TryParseJson(result.Stdout, out var payload))
        {
            var err = string.IsNullOrWhiteSpace(result.Stderr) ? result.Stdout : result.Stderr;
            return ToolResult.Failure(
                "Browser navigate failed",
                string.IsNullOrWhiteSpace(err) ? $"exit {result.ExitCode}" : err.Trim());
        }

        if (!payload.TryGetProperty("ok", out var okEl) || !okEl.GetBoolean())
        {
            var error = payload.TryGetProperty("error", out var errEl) ? errEl.GetString() : "unknown error";
            return ToolResult.Failure("Browser navigate failed", error ?? "unknown error");
        }

        var pageUrl = payload.TryGetProperty("url", out var urlEl) ? urlEl.GetString() ?? "" : "";
        var title = payload.TryGetProperty("title", out var titleEl) ? titleEl.GetString() ?? "" : "";
        var summary = actionLabel == "navigate"
            ? $"Navigated to {pageUrl}"
            : $"Browser {actionLabel}";
        var content =
            $"url={pageUrl}\ntitle={title}\n" +
            "Sandbox Chromium is ready. Prefer https:// URLs; workspace files use file:///workspace/...";
        return ToolResult.Success(summary, content);
    }

    private static string NormalizeNavigateUrl(string raw)
    {
        var url = raw.Trim();
        var map = RoomAgentTurnScope.Current?.Options.PathMap;

        // Host absolute path → file:///workspace/...
        if (map is not null
            && (url.Contains(Path.DirectorySeparatorChar) || url.Contains(Path.AltDirectorySeparatorChar)
                || Path.IsPathRooted(url))
            && !url.Contains("://", StringComparison.Ordinal))
        {
            try
            {
                var full = Path.GetFullPath(url);
                var container = map.ToContainer(full);
                if (container is not null)
                {
                    return "file://" + container;
                }
            }
            catch
            {
                // fall through
            }
        }

        // file:///C:/... or file:///Users/... under mounts
        if (map is not null
            && url.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            var pathPart = url["file:".Length..].TrimStart('/');
            if (OperatingSystem.IsWindows() && pathPart.Length >= 2 && pathPart[1] == ':')
            {
                // file:///C:/foo → C:/foo
                var hostPath = pathPart;
                try
                {
                    var container = map.ToContainer(Path.GetFullPath(hostPath));
                    if (container is not null)
                    {
                        return "file://" + container;
                    }
                }
                catch
                {
                    // fall through
                }
            }
        }

        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        // Bare host → https
        return "https://" + url.TrimStart('/');
    }

    private static bool TryParseJson(string text, out JsonElement element)
    {
        element = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(text.Trim());
            element = doc.RootElement.Clone();
            return true;
        }
        catch
        {
            // Chromium may print noise; try last JSON object line
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                         .Reverse())
            {
                if (!line.StartsWith('{'))
                {
                    continue;
                }

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    element = doc.RootElement.Clone();
                    return true;
                }
                catch
                {
                    // continue
                }
            }

            return false;
        }
    }

    private static string ShellQuote(string value) =>
        "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
