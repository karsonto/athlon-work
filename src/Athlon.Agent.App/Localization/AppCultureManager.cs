using System.Globalization;
using Athlon.Agent.App.Resources;
using Athlon.Agent.Core;

namespace Athlon.Agent.App.Localization;

/// <summary>Applies UI culture from settings and notifies consumers (mirrors <see cref="Themes.AppThemeManager"/>).</summary>
public static class AppCultureManager
{
    private static readonly CultureInfo FallbackCulture = CultureInfo.GetCultureInfo("zh-CN");

    private static readonly HashSet<string> SupportedCultures = new(StringComparer.OrdinalIgnoreCase)
    {
        "zh-CN",
        "en-US",
    };

    public static CultureInfo Current { get; private set; } = FallbackCulture;

    public static event EventHandler? CultureChanged;

    public static void ApplyFromSettings(UiSettings ui) => SetCulture(ui.Language, ui);

    public static void SetCulture(string? language, UiSettings? ui = null)
    {
        var resolved = ResolveCulture(language);
        if (ui is not null)
        {
            ui.Language = NormalizeLanguageSetting(language);
        }

        if (resolved.Name.Equals(Current.Name, StringComparison.OrdinalIgnoreCase)
            && Strings.Culture?.Name.Equals(resolved.Name, StringComparison.OrdinalIgnoreCase) == true)
        {
            return;
        }

        Current = resolved;
        Strings.Culture = resolved;
        CultureInfo.CurrentUICulture = resolved;
        CultureInfo.CurrentCulture = resolved;
        CultureChanged?.Invoke(null, EventArgs.Empty);
    }

    public static CultureInfo ResolveCulture(string? language)
    {
        var normalized = NormalizeLanguageSetting(language);
        return SupportedCultures.Contains(normalized)
            ? CultureInfo.GetCultureInfo(normalized)
            : FallbackCulture;
    }

    public static string NormalizeLanguageSetting(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return "zh-CN";
        }

        var trimmed = language.Trim();
        if (trimmed.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            return "zh-CN";
        }

        if (trimmed.Equals("zh-CN", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("zh", StringComparison.OrdinalIgnoreCase))
        {
            return "zh-CN";
        }

        if (trimmed.Equals("en-US", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("en", StringComparison.OrdinalIgnoreCase))
        {
            return "en-US";
        }

        return SupportedCultures.Contains(trimmed) ? trimmed : "zh-CN";
    }

    public static IReadOnlyList<LanguageOption> GetLanguageOptions() =>
    [
        new("zh-CN", Strings.Get("Settings_Language_zh_CN")),
        new("en-US", Strings.Get("Settings_Language_en_US")),
    ];
}

public sealed record LanguageOption(string Value, string DisplayName);
