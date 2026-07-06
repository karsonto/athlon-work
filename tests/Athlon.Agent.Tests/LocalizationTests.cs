using Athlon.Agent.App.Localization;
using Athlon.Agent.App.Resources;
using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

public sealed class LocalizationTests
{
    [Fact]
    public void UiSettings_default_language_is_zhCN()
    {
        var ui = new UiSettings();

        Assert.Equal("zh-CN", ui.Language);
    }

    [Fact]
    public void ApplyFromSettings_with_default_ui_uses_chinese_strings()
    {
        var ui = new UiSettings();
        AppCultureManager.ApplyFromSettings(ui);

        Assert.Equal("zh-CN", AppCultureManager.Current.Name);
        Assert.Equal("确定", Strings.Get("Common_OK"));
    }

    [Fact]
    public void SetCulture_enUS_returns_english_strings()
    {
        var ui = new UiSettings { Language = "en-US" };
        AppCultureManager.SetCulture("en-US", ui);

        Assert.Equal("OK", Strings.Get("Common_OK"));
        Assert.Equal("Today", Strings.Get("RecordGroup_Today"));
    }

    [Fact]
    public void SetCulture_zhCN_returns_chinese_strings()
    {
        AppCultureManager.SetCulture("zh-CN");

        Assert.Equal("确定", Strings.Get("Common_OK"));
        Assert.Equal("今天", Strings.Get("RecordGroup_Today"));
    }

    [Fact]
    public void Format_replaces_placeholders()
    {
        AppCultureManager.SetCulture("en-US");

        var text = Strings.Format("Shell_DeleteConversationMessage", "My chat");

        Assert.Contains("My chat", text, StringComparison.Ordinal);
    }

    [Fact]
    public void GetLanguageOptions_excludes_auto_and_includes_zhCN_and_enUS()
    {
        AppCultureManager.SetCulture("zh-CN");

        var options = AppCultureManager.GetLanguageOptions();

        Assert.Equal(2, options.Count);
        Assert.Equal("zh-CN", options[0].Value);
        Assert.Equal("en-US", options[1].Value);
        Assert.DoesNotContain(options, option => option.Value.Equals("Auto", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NormalizeLanguageSetting_maps_auto_and_empty_to_zhCN()
    {
        Assert.Equal("zh-CN", AppCultureManager.NormalizeLanguageSetting(null));
        Assert.Equal("zh-CN", AppCultureManager.NormalizeLanguageSetting(""));
        Assert.Equal("zh-CN", AppCultureManager.NormalizeLanguageSetting("Auto"));
    }

    [Fact]
    public void ResolveCulture_maps_auto_to_zhCN()
    {
        Assert.Equal("zh-CN", AppCultureManager.ResolveCulture("Auto").Name);
    }

    [Fact]
    public void SetCulture_migrates_auto_setting_to_zhCN()
    {
        var ui = new UiSettings { Language = "Auto" };
        AppCultureManager.SetCulture("Auto", ui);

        Assert.Equal("zh-CN", ui.Language);
        Assert.Equal("zh-CN", AppCultureManager.Current.Name);
    }

    [Fact]
    public void EnUs_resx_contains_all_default_keys()
    {
        AppCultureManager.SetCulture("zh-CN");
        var assembly = typeof(Strings).Assembly;
        var zhManager = new System.Resources.ResourceManager(
            "Athlon.Agent.App.Resources.Strings",
            assembly);
        var enManager = new System.Resources.ResourceManager(
            "Athlon.Agent.App.Resources.Strings",
            assembly);

        var zhSet = zhManager.GetResourceSet(
            System.Globalization.CultureInfo.InvariantCulture,
            createIfNotExists: true,
            tryParents: true)!;
        var enCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        var enSet = enManager.GetResourceSet(
            enCulture,
            createIfNotExists: true,
            tryParents: false);

        Assert.NotNull(enSet);

        foreach (System.Collections.DictionaryEntry entry in zhSet)
        {
            var key = (string)entry.Key;
            var enValue = enSet.GetString(key);
            if (enValue is null)
            {
                enValue = enManager.GetString(key, enCulture);
            }

            Assert.False(string.IsNullOrEmpty(enValue), $"Missing en-US value for key '{key}'");
        }
    }
}
