using System.Diagnostics;
using System.Text;
using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure;

internal static class RipgrepRunner
{
    public static bool IsAvailable() =>
        !string.IsNullOrWhiteSpace(ResolveExecutable());

    public static async Task<ToolResult?> TrySearchAsync(
        string pattern,
        string searchRoot,
        string workspaceRoot,
        string glob,
        bool useRegex,
        IReadOnlyList<string> ignorePatterns,
        CancellationToken cancellationToken)
    {
        var rg = ResolveExecutable();
        if (rg is null)
        {
            return null;
        }

        var args = new StringBuilder();
        args.Append("-n --no-heading --color=never --max-count=200 ");
        if (!useRegex)
        {
            args.Append("-F ");
        }

        if (!string.Equals(glob, "*", StringComparison.Ordinal))
        {
            args.Append($"--glob {Quote(glob)} ");
        }

        foreach (var ignored in ignorePatterns)
        {
            if (!string.IsNullOrWhiteSpace(ignored))
            {
                args.Append($"--glob !{Quote("**/" + ignored.Trim('/') + "/**")} ");
            }
        }

        args.Append($"{Quote(pattern)} {Quote(searchRoot)}");

        var psi = new ProcessStartInfo
        {
            FileName = rg,
            Arguments = args.ToString(),
            WorkingDirectory = workspaceRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = Process.Start(psi);
        if (process is null)
        {
            return null;
        }

        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode is 1)
        {
            return ToolResult.Success("No matches found", "No matches found");
        }

        if (process.ExitCode != 0)
        {
            return ToolResult.Failure("ripgrep failed", string.IsNullOrWhiteSpace(stderr) ? $"exit {process.ExitCode}" : stderr.Trim());
        }

        var lines = stdout.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var relativeLines = lines.Select(line => RelativizeLine(line, workspaceRoot)).ToArray();
        return relativeLines.Length == 0
            ? ToolResult.Success("No matches found", "No matches found")
            : ToolResult.Success($"Found {relativeLines.Length} matches", string.Join(Environment.NewLine, relativeLines));
    }

    private static string RelativizeLine(string line, string workspaceRoot)
    {
        var separator = line.IndexOf(':');
        if (separator <= 0)
        {
            return line;
        }

        var filePart = line[..separator];
        if (!Path.IsPathRooted(filePart))
        {
            return line;
        }

        try
        {
            var relative = Path.GetRelativePath(workspaceRoot, filePart).Replace('\\', '/');
            return relative + line[separator..];
        }
        catch
        {
            return line;
        }
    }

    private static string? ResolveExecutable()
    {
        foreach (var candidate in new[] { "rg.exe", "rg" })
        {
            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var segment in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var full = Path.Combine(segment.Trim(), candidate);
                if (File.Exists(full))
                {
                    return full;
                }
            }
        }

        return null;
    }

    private static string Quote(string value) => value.Contains(' ') || value.Contains('"')
        ? $"\"{value.Replace("\"", "\\\"")}\""
        : value;
}
