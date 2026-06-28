using System.Globalization;
using Athlon.Agent.App.Resources;

namespace Athlon.Agent.App.Localization;

public sealed class LocalizationService : ILocalizationService
{
    public string this[string key] => Strings.Get(key);

    public string Format(string key, params object[] args) => Strings.Format(key, args);

    public CultureInfo CurrentCulture => AppCultureManager.Current;
}
