using System.ComponentModel;
using System.Runtime.CompilerServices;
using Athlon.Agent.App.Resources;

namespace Athlon.Agent.App.Localization;

/// <summary>Binding source for XAML localization markup; refreshes when culture changes.</summary>
public sealed class LocalizationHub : INotifyPropertyChanged
{
    public static LocalizationHub Instance { get; } = new();

    static LocalizationHub()
    {
        AppCultureManager.CultureChanged += (_, _) => Instance.NotifyAll();
    }

    public string this[string key] => Strings.Get(key);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void NotifyAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    private LocalizationHub()
    {
    }
}
