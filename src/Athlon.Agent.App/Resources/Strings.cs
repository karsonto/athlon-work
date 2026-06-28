using System.Globalization;
using System.Resources;

namespace Athlon.Agent.App.Resources;

public static class Strings
{
    private static readonly ResourceManager Manager = new(
        "Athlon.Agent.App.Resources.Strings",
        typeof(Strings).Assembly);

    public static CultureInfo? Culture { get; set; }

    public static string Get(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Manager.GetString(name, Culture ?? CultureInfo.CurrentUICulture) ?? name;
    }

    public static string Format(string name, params object[] args)
    {
        var format = Get(name);
        return string.Format(Culture ?? CultureInfo.CurrentUICulture, format, args);
    }
}
