using System.Globalization;

namespace Athlon.Agent.App.Localization;

public interface ILocalizationService
{
    string this[string key] { get; }

    string Format(string key, params object[] args);

    CultureInfo CurrentCulture { get; }
}
