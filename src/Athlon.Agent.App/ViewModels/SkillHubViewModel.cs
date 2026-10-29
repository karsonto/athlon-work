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
            var availability = await ClassifyAllAsync(cancellationToken).ConfigureAwait(true);
            EmitCatalog(availability);
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

            var fullDownload = !root.TryGetProperty("mode", out var modeEl)
                || !string.Equals(modeEl.GetString(), SkillInstallClassifier.Update, StringComparison.OrdinalIgnoreCase);
            await InstallAsync(id, fullDownload, cancellationToken).ConfigureAwait(true);
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

    private async Task InstallAsync(string skillId, bool fullDownload, CancellationToken cancellationToken)
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
            await _installer.InstallAsync(skill, fullDownload, cancellationToken).ConfigureAwait(true);
            _catalog.Reload();
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

    private async Task<IReadOnlyDictionary<string, string>> ClassifyAllAsync(CancellationToken cancellationToken)
    {
        var availability = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in _items)
        {
            availability[item.Id] = await ClassifyAsync(item, cancellationToken).ConfigureAwait(true);
        }

        return availability;
    }

    private async Task<string> ClassifyAsync(RemoteSkillDto item, CancellationToken cancellationToken)
    {
        var directory = SkillInstallClassifier.FindInstalledDirectory(_paths.SkillsPath, item);
        if (directory is null)
        {
            return SkillInstallClassifier.Use;
        }

        try
        {
            var manifestText = (await _client.GetManifestAsync(item.Id, cancellationToken).ConfigureAwait(true))
                .TrimEnd('\r', '\n');
            var anchorId = SkillManifest.TryParse(manifestText, out var manifest) && manifest is not null
                ? manifest.SkillId
                : item.Id;
            var anchor = SkillAnchorStore.Get(_paths.SkillAnchorsPath, anchorId);
            return SkillInstallClassifier.Classify(directory, manifestText, anchor);
        }
        catch (Exception)
        {
            return SkillInstallClassifier.Installed;
        }
    }

    private void EmitCatalog(IReadOnlyDictionary<string, string> availability)
    {
        var payload = new
        {
            type = "catalog",
            items = _items.Select(item => ToWireItem(
                item,
                availability.TryGetValue(item.Id, out var action) ? action : SkillInstallClassifier.Use)),
            installed = Array.Empty<string>(),
            emptyMessage = _localization["SkillHub_Empty"]
        };
        CatalogJsonReady?.Invoke(this, JsonSerializer.Serialize(payload, AppJson.Options));
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
        CatalogJsonReady?.Invoke(this, JsonSerializer.Serialize(payload, AppJson.Options));
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
        CatalogJsonReady?.Invoke(this, JsonSerializer.Serialize(payload, AppJson.Options));
    }

    private static object ToWireItem(RemoteSkillDto item, string availability) =>
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
            commitSeq = item.CommitSeq,
            download = item.Download,
            availability,
            installed = string.Equals(availability, SkillInstallClassifier.Installed, StringComparison.Ordinal)
        };

    [RelayCommand]
    private Task RefreshCommand() => RefreshAsync();
}
