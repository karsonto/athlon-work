using System.Text.Encodings.Web;
using System.Text.Json;
using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure;

public interface IAppPathProvider
{
    string RootPath { get; }
    string ConfigPath { get; }
    string SessionsPath { get; }
    string AuditPath { get; }
    string LogsPath { get; }
    string CredentialsPath { get; }
    string SkillsPath { get; }

    void EnsureCreated();

    string ResolveSkillPath(string path);
}

public sealed class AppPathProvider : IAppPathProvider
{
    public const string AppDataFolderName = ".athlon-agent";
    public const string SkillsFolderName = "skills";

    public string RootPath { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), AppDataFolderName);
    public string ConfigPath => Path.Combine(RootPath, "config");
    public string SessionsPath => Path.Combine(RootPath, "sessions");
    public string AuditPath => Path.Combine(RootPath, "audit");
    public string LogsPath => Path.Combine(RootPath, "logs");
    public string CredentialsPath => Path.Combine(RootPath, "credentials");
    public string SkillsPath => Path.Combine(RootPath, SkillsFolderName);

    public void EnsureCreated()
    {
        Directory.CreateDirectory(RootPath);
        Directory.CreateDirectory(ConfigPath);
        Directory.CreateDirectory(SessionsPath);
        Directory.CreateDirectory(AuditPath);
        Directory.CreateDirectory(LogsPath);
        Directory.CreateDirectory(CredentialsPath);
        Directory.CreateDirectory(SkillsPath);
    }

    public string ResolveSkillPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        return Path.IsPathRooted(path) ? path : Path.Combine(SkillsPath, path);
    }
}

public interface IJsonFileStore
{
    Task SaveAsync<T>(string path, T value, CancellationToken cancellationToken = default);
    Task<T?> LoadAsync<T>(string path, CancellationToken cancellationToken = default);
    Task AppendJsonLineAsync(string path, object value, CancellationToken cancellationToken = default, bool prettyPrint = false);
}

public sealed class JsonFileStore : IJsonFileStore
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        // Persist UTF-8 Chinese (and other non-ASCII) literally in session.json instead of \uXXXX.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Single-line JSON for machine-friendly append logs (conversation/tool/audit).</summary>
    public static readonly JsonSerializerOptions JsonLineOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public Task SaveAsync<T>(string path, T value, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(value, Options);
        return FileIoRetry.RunAsync(
            () => AtomicFile.WriteAllTextAsync(path, json, cancellationToken),
            cancellationToken);
    }

    public async Task<T?> LoadAsync<T>(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, Options, cancellationToken);
    }

    public async Task AppendJsonLineAsync(string path, object value, CancellationToken cancellationToken = default, bool prettyPrint = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var options = prettyPrint ? Options : JsonLineOptions;
        var line = JsonSerializer.Serialize(value, options) + Environment.NewLine;
        await FileIoRetry.RunAsync(
            async () => await File.AppendAllTextAsync(path, line, cancellationToken).ConfigureAwait(false),
            cancellationToken);
    }
}

public static class AtomicFile
{
    public static Task WriteAllTextAsync(string path, string content, CancellationToken cancellationToken = default) =>
        FileIoRetry.RunAsync(
            () => WriteAllTextCoreAsync(path, content, cancellationToken),
            cancellationToken);

    private static async Task WriteAllTextCoreAsync(string path, string content, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, content, cancellationToken).ConfigureAwait(false);
        BackupIfExists(path);
        File.Move(temp, path, true);
    }

    public static void BackupIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Copy(path, path + ".bak", true);
        }
    }
}

public static class SessionMarkdownWriter
{
    public static string WriteConversation(AgentSession session)
    {
        var lines = new List<string>
        {
            $"# {session.Title}",
            "",
            $"- Session: `{session.Id}`",
            $"- Created: `{session.CreatedAt:u}`",
            $"- Updated: `{session.UpdatedAt:u}`",
            ""
        };

        foreach (var message in session.Messages)
        {
            var roleLabel = message.Role == MessageRole.Compaction ? "Compaction" : message.Role.ToString();
            lines.Add($"## {roleLabel} - {message.CreatedAt:u}");
            lines.Add("");
            if (message.ImageAttachments is { Count: > 0 })
            {
                lines.Add($"附图: {string.Join(", ", message.ImageAttachments.Select(image => image.FileName))}");
                lines.Add("");
            }
            lines.Add(message.Content);
            lines.Add("");
        }

        return string.Join(Environment.NewLine, lines);
    }

    public static string WriteSummary(ContextSummary summary) =>
        $"# Context Summary\n\n- Session: `{summary.SessionId}`\n- Created: `{summary.CreatedAt:u}`\n- Original messages: `{summary.OriginalMessageCount}`\n\n{summary.Content}\n";
}

public static class FileNameSanitizer
{
    public static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(ch => invalid.Contains(ch) ? '-' : ch));
    }
}

public static class ToolArguments
{
    public static bool TryGetRequired(ToolInvocation invocation, string name, out string value, out ToolResult error)
    {
        if (invocation.Arguments.TryGetValue(name, out value!) && !string.IsNullOrWhiteSpace(value))
        {
            error = ToolResult.Success("OK");
            return true;
        }

        error = ToolResult.Failure("Missing argument", $"{invocation.ToolName} requires `{name}`.");
        return false;
    }

    public static int GetInt32(ToolInvocation invocation, string name, int defaultValue) =>
        invocation.Arguments.TryGetValue(name, out var value) && int.TryParse(value, out var parsed) ? parsed : defaultValue;

    public static bool TryGetNormalizedPath(ToolInvocation invocation, out string path, out ToolResult error)
    {
        if (!TryGetRequired(invocation, ToolPathNormalizer.PathArgumentName, out var raw, out error))
        {
            path = string.Empty;
            return false;
        }

        if (!ToolPathNormalizer.TryNormalizeForFileOperation(raw, out path, out var message))
        {
            path = string.Empty;
            error = ToolResult.Failure("Invalid path", $"{invocation.ToolName}: {message}");
            return false;
        }

        error = ToolResult.Success("OK");
        return true;
    }

    public static bool TryGetOptionalNormalizedPath(
        ToolInvocation invocation,
        out string path,
        out ToolResult error,
        string defaultPath = ".")
    {
        if (!invocation.Arguments.TryGetValue(ToolPathNormalizer.PathArgumentName, out var raw)
            || string.IsNullOrWhiteSpace(raw))
        {
            path = ToolPathNormalizer.ForModel(defaultPath);
            error = ToolResult.Success("OK");
            return true;
        }

        if (!ToolPathNormalizer.TryNormalizeForFileOperation(raw, out path, out var message))
        {
            path = string.Empty;
            error = ToolResult.Failure("Invalid path", $"{invocation.ToolName}: {message}");
            return false;
        }

        error = ToolResult.Success("OK");
        return true;
    }

    /// <summary>Resolves execute_command cwd: workspace root by default, workspace-relative when set.</summary>
    public static bool TryResolveWorkingDirectory(
        ToolInvocation invocation,
        WorkspaceGuard guard,
        out string fullPath,
        out ToolResult error)
    {
        if (guard.HasConfiguredWorkspace)
        {
            try
            {
                if (!invocation.Arguments.TryGetValue(ToolPathNormalizer.CwdArgumentName, out var raw)
                    || string.IsNullOrWhiteSpace(raw))
                {
                    fullPath = guard.Normalize(".");
                    error = ToolResult.Success("OK");
                    return true;
                }

                if (!ToolPathNormalizer.TryNormalizeForFileOperation(raw, out var normalized, out var message))
                {
                    fullPath = string.Empty;
                    error = ToolResult.Failure("Invalid working directory", $"{invocation.ToolName}: {message}");
                    return false;
                }

                fullPath = guard.Normalize(normalized);
                if (!guard.IsInsideWorkspace(fullPath))
                {
                    error = ToolResult.Failure("Outside workspace", fullPath);
                    fullPath = string.Empty;
                    return false;
                }

                if (!Directory.Exists(fullPath))
                {
                    error = ToolResult.Failure(
                        "Invalid working directory",
                        $"Working directory does not exist: {ToolPathNormalizer.ForModel(normalized)}");
                    fullPath = string.Empty;
                    return false;
                }

                error = ToolResult.Success("OK");
                return true;
            }
            catch (InvalidOperationException ex)
            {
                fullPath = string.Empty;
                error = ToolResult.Failure("Workspace not configured", ex.Message);
                return false;
            }
        }

        var cwd = invocation.Arguments.GetValueOrDefault(ToolPathNormalizer.CwdArgumentName);
        if (string.IsNullOrWhiteSpace(cwd))
        {
            fullPath = Environment.CurrentDirectory;
            error = ToolResult.Success("OK");
            return true;
        }

        if (!ToolPathNormalizer.TryNormalizeForFileOperation(cwd, out var normalizedCwd, out var cwdMessage))
        {
            fullPath = string.Empty;
            error = ToolResult.Failure("Invalid working directory", $"{invocation.ToolName}: {cwdMessage}");
            return false;
        }

        var rooted = Path.GetFullPath(normalizedCwd.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(rooted))
        {
            error = ToolResult.Failure(
                "Invalid working directory",
                $"Working directory does not exist: {normalizedCwd}");
            fullPath = string.Empty;
            return false;
        }

        fullPath = rooted;
        error = ToolResult.Success("OK");
        return true;
    }
}

public static class SensitiveText
{
    private static readonly string[] Tokens = { "Authorization", "api_key", "apikey", "token", "password", "secret" };

    public static string Redact(string message)
    {
        return Tokens.Aggregate(message, (current, token) =>
            current.Replace(token, $"{token}[redacted]", StringComparison.OrdinalIgnoreCase));
    }
}
