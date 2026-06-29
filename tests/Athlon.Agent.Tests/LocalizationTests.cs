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
