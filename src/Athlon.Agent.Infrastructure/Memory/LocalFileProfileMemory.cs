using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Memory;

namespace Athlon.Agent.Infrastructure.Memory;

/// <summary>
/// Local file backend for the account-level habit profile, stored under
/// <c>~/.athlon-agent/profile/{USER.md, LESSONS.md}</c>.
///
/// Writes are serialized so two sessions flushing concurrently cannot interleave on the same file,
/// and each write is atomic (<see cref="AtomicFile"/> uses a unique temp sibling plus
/// <c>File.Move(overwrite: true)</c>), so a reader never observes a half-written document.
/// </summary>
public sealed class LocalFileProfileMemory(
    IAppPathProvider paths,
    AppSettings settings,
    IAppLogger logger) : IProfileMemory
{
    private readonly IAppLogger _logger = logger.ForContext("LocalFileProfileMemory");
    private readonly ProfileSettings _cfg = settings.Profile;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public ProfileCapabilities Capabilities =>
        new(ProfileBackendKind.Local, CanWrite: true, CanDelete: true, IsRemote: false);

    /// <summary>Absolute directory holding the profile documents.</summary>
    public string DirectoryPath => Path.Combine(paths.RootPath, _cfg.DirName);

    public string ResolvePath(ProfileKind kind) =>
        Path.Combine(DirectoryPath, ProfileKinds.FileName(kind));

    public async Task<ProfileDocument?> ReadAsync(
        ProfileKind kind,
        CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(kind);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var content = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken)
                .ConfigureAwait(false);
            return new ProfileDocument(
                kind,
                content,
                new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero),
                ProfileBackendKind.Local);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warning("Failed to read profile document {Path}: {Message}", path, ex.Message);
            return null;
        }
    }

    public async Task WriteAsync(
        ProfileKind kind,
        string content,
        CancellationToken cancellationToken = default)
    {
        if (!Capabilities.CanWrite)
        {
            return;
        }

        var trimmed = EnforceMaxChars(kind, content ?? string.Empty);
        var path = ResolvePath(kind);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            await AtomicFile.WriteAllTextAsync(path, trimmed, cancellationToken).ConfigureAwait(false);
            _logger.Debug("Wrote profile document {Path} ({Chars} chars)", path, trimmed.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warning("Failed to write profile document {Path}: {Message}", path, ex.Message);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> DeleteAsync(ProfileKind kind, CancellationToken cancellationToken = default)
    {
        if (!Capabilities.CanDelete)
        {
            return false;
        }

        var path = ResolvePath(kind);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            _logger.Information("Deleted profile document {Path}", path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warning("Failed to delete profile document {Path}: {Message}", path, ex.Message);
            return false;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Trims a document to the configured budget by keeping whole <c>## </c> sections. The first
    /// line (the document title) is always kept, and a truncated document ends with an explicit
    /// marker instead of a half sentence.
    /// </summary>
    public string EnforceMaxChars(ProfileKind kind, string content)
    {
        var maxChars = Math.Max(1, _cfg.MaxChars(kind));
        if (content.Length <= maxChars)
        {
            return content;
        }

        var lines = content.Replace("\r\n", "\n").Split('\n');
        var header = new StringBuilder();
        var sections = new List<string>();
        var current = new StringBuilder();
        var sawSection = false;

        foreach (var line in lines)
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (sawSection)
                {
                    sections.Add(current.ToString().TrimEnd());
                }

                current.Clear();
                sawSection = true;
            }

            if (sawSection)
            {
                current.AppendLine(line);
            }
            else
            {
                header.AppendLine(line);
            }
        }

        if (sawSection)
        {
            sections.Add(current.ToString().TrimEnd());
        }

        const string marker = "\n\n...(truncated)";
        var budget = Math.Max(0, maxChars - marker.Length);

        var built = new StringBuilder(header.ToString().TrimEnd());
        if (!sawSection)
        {
            // No sections at all: keep whole lines only.
            built.Clear();
            foreach (var line in lines)
            {
                if (built.Length + line.Length + 1 > budget)
                {
                    break;
                }

                built.AppendLine(line);
            }

            return built.ToString().TrimEnd() + marker;
        }

        foreach (var section in sections)
        {
            if (built.Length + section.Length + 2 > budget)
            {
                break;
            }

            built.Append("\n\n").Append(section);
        }

        return built.ToString().TrimEnd() + marker;
    }
}
