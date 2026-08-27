using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Athlon.Agent.App.Localization;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Infrastructure.SkillHub;
using Athlon.Agent.Skills;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Athlon.Agent.App.ViewModels;

public sealed partial class SkillHubViewModel : ObservableObject
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ISkillHubClient _client;
    private readonly SkillPackageInstaller _installer;
    private readonly IAgentSkillCatalog _catalog;
    private readonly IAppPathProvider _paths;
    private readonly ILocalizationService _localization;
    private readonly IUserNotifier _notifier;
    private Action _onSkillsInstalled = () => { };
    private Action _navigateToSettings = () => { };
    private IReadOnlyList<RemoteSkillDto> _items = [];

    public SkillHubViewModel(
        ISkillHubClient client,
        SkillPackageInstaller installer,
        IAgentSkillCatalog catalog,
        IAppPathProvider paths,
        ILocalizationService localization,
        IUserNotifier notifier)
    {
        _client = client;
        _installer = installer;
        _catalog = catalog;
        _paths = paths;
        _localization = localization;
        _notifier = notifier;
    }

    public void Configure(Action onSkillsInstalled, Action navigateToSettings)
    {
        _onSkillsInstalled = onSkillsInstalled;
        _navigateToSettings = navigateToSettings;
    }

    public event EventHandler<string>? CatalogJsonReady;

    [ObservableProperty]
    private bool isLoading;

    [ObservableProperty]
    private string? lastError;

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        LastError = null;
        try
        {
            if (!_client.IsConfigured)
            {
                EmitCatalogError(_localization["SkillHub_NoServer"]);
                return;
            }

            _items = await _client.ListAsync(cancellationToken).ConfigureAwait(true);
            EmitCatalog();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            EmitCatalogError(ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task HandleWebMessageAsync(string json, CancellationToken cancellationToken = default)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        // Host may pass an already-unwrapped object; tolerate a stringified payload too.
        if (root.ValueKind == JsonValueKind.String)
        {
            var inner = root.GetString();
            if (string.IsNullOrWhiteSpace(inner))
            {
                return;
            }

            await HandleWebMessageAsync(inner, cancellationToken).ConfigureAwait(true);
            return;
        }

        if (!root.TryGetProperty("type", out var typeEl))
        {
            return;
        }

        var type = typeEl.GetString();
        if (string.Equals(type, "ready", StringComparison.Ordinal))
        {
            await RefreshAsync(cancellationToken).ConfigureAwait(true);
            return;
        }

        if (string.Equals(type, "manage", StringComparison.Ordinal))
        {
            _navigateToSettings();
            return;
        }

        if (string.Equals(type, "add", StringComparison.Ordinal))
        {
            var id = ReadWireId(root);
            if (string.IsNullOrWhiteSpace(id))
            {
                EmitInstallResult("", ok: false, error: _localization["SkillHub_SkillNotFound"]);
                _notifier.WarningText("SkillHub_Title", _localization["SkillHub_SkillNotFound"]);
                return;
            }

            await InstallAsync(id, cancellationToken).ConfigureAwait(true);
        }
    }

    /// <summary>Reads <c>id</c> from a web message whether it is a JSON string or number.</summary>
    public static string? ReadWireId(JsonElement root)
    {
        if (!root.TryGetProperty("id", out var idEl))
        {
            return null;
        }

        return idEl.ValueKind switch
        {
            JsonValueKind.String => idEl.GetString(),
            JsonValueKind.Number => idEl.GetRawText(),
            JsonValueKind.True or JsonValueKind.False => idEl.GetRawText(),
            _ => idEl.ToString()
        };
    }

    private async Task InstallAsync(string skillId, CancellationToken cancellationToken)
    {
        var skill = _items.FirstOrDefault(item =>
            string.Equals(item.Id, skillId, StringComparison.Ordinal)
            || string.Equals(item.EnglishName, skillId, StringComparison.OrdinalIgnoreCase));
        if (skill is null)
        {
            var notFound = _localization["SkillHub_SkillNotFound"];
            EmitInstallResult(skillId, ok: false, error: notFound);
            _notifier.WarningText("SkillHub_Title", notFound);
            return;
        }

        try
        {
            await _installer.InstallAsync(skill, cancellationToken).ConfigureAwait(true);
            // Notify WebView before sidebar refresh so Adding cannot stick if UI work is slow.
            EmitInstallResult(skillId, ok: true, englishName: skill.EnglishName, name: skill.Name);
            _notifier.InfoText("SkillHub_Title", _localization.Format("SkillHub_InstallSuccess", skill.Name));
            try
            {
                _onSkillsInstalled();
            }
            catch (Exception ex)
            {
                App.StartupTrace($"SkillHub post-install refresh failed: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            EmitInstallResult(skillId, ok: false, error: ex.Message, englishName: skill.EnglishName, name: skill.Name);
            _notifier.WarningText("SkillHub_Title", ex.Message);
        }
    }

    private void EmitCatalog()
    {
        var installedKeys = CollectInstalledKeys();
        var payload = new
        {
            type = "catalog",
            items = _items.Select(item => ToWireItem(item, IsInstalled(item, installedKeys))),
            installed = installedKeys.ToList(),
            emptyMessage = _localization["SkillHub_Empty"]
        };
        CatalogJsonReady?.Invoke(this, JsonSerializer.Serialize(payload, JsonOptions));
    }

    private void EmitCatalogError(string error)
    {
        var payload = new
        {
            type = "catalog",
            items = Array.Empty<object>(),
            installed = Array.Empty<string>(),
            error
        };
        CatalogJsonReady?.Invoke(this, JsonSerializer.Serialize(payload, JsonOptions));
    }

    private void EmitInstallResult(
        string id,
        bool ok,
        string? error = null,
        string? englishName = null,
        string? name = null)
    {
        var payload = new
        {
            type = "installResult",
            id,
            ok,
            error,
            englishName,
            name
        };
        CatalogJsonReady?.Invoke(this, JsonSerializer.Serialize(payload, JsonOptions));
    }

    /// <summary>
    /// Keys used to match remote hub items to local installs: folder names, SKILL.md names,
    /// and sanitized englishName targets used by <see cref="SkillPackageInstaller"/>.
    /// </summary>
    private HashSet<string> CollectInstalledKeys()
    {
        _catalog.Reload();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var skill in _catalog.Skills)
        {
            AddKey(keys, skill.Name);
            AddKey(keys, skill.SkillId);
            if (!string.IsNullOrWhiteSpace(skill.SkillDirectory))
            {
                AddKey(keys, Path.GetFileName(skill.SkillDirectory.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar)));
            }
        }

        if (!Directory.Exists(_paths.SkillsPath))
        {
            return keys;
        }

        foreach (var dir in Directory.EnumerateDirectories(_paths.SkillsPath))
        {
            if (!SkillFileSystemHelper.HasSkillFile(dir))
            {
                continue;
            }

            AddKey(keys, Path.GetFileName(dir));
            try
            {
                var loaded = SkillFileSystemHelper.LoadSkillFromDirectory(dir);
                AddKey(keys, loaded.Name);
            }
            catch
            {
                // skip unreadable folders
            }
        }

        return keys;
    }

    private bool IsInstalled(RemoteSkillDto item, HashSet<string> installedKeys)
    {
        if (installedKeys.Count == 0)
        {
            return false;
        }

        foreach (var candidate in EnumerateMatchKeys(item))
        {
            if (installedKeys.Contains(candidate))
            {
                return true;
            }

            // Folder may have been sanitized on install (path separators → _).
            try
            {
                var sanitized = SkillPackageInstaller.SanitizeFolderName(candidate);
                if (installedKeys.Contains(sanitized))
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                // ignore invalid names
            }

            if (SkillFileSystemHelper.SkillExists(_paths.SkillsPath, candidate))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> EnumerateMatchKeys(RemoteSkillDto item)
    {
        if (!string.IsNullOrWhiteSpace(item.EnglishName))
        {
            yield return item.EnglishName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(item.Name))
        {
            yield return item.Name.Trim();
        }

        if (!string.IsNullOrWhiteSpace(item.Id))
        {
            yield return item.Id.Trim();
        }
    }

    private static void AddKey(HashSet<string> keys, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            keys.Add(value.Trim());
        }
    }

    private static object ToWireItem(RemoteSkillDto item, bool installed) =>
        new
        {
            id = item.Id,
            englishName = item.EnglishName,
            name = item.Name,
            description = item.Description,
            category = item.Category,
            position = item.Position,
            packageSize = item.PackageSize,
            packageSha256 = item.PackageSha256,
            download = item.Download,
            installed
        };

    [RelayCommand]
    private Task RefreshCommand() => RefreshAsync();
}
