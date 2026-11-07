using System.Globalization;
using System.Resources;
using System.Globalization;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Markup;

namespace RiotSwitcher;

public static class Localization
{
    private static readonly ResourceManager ResourceManager =
        new("RiotSwitcher.Properties.Resources", typeof(Localization).Assembly);

    public static CultureInfo CurrentCulture { get; set; } = CultureInfo.CurrentUICulture;

    public static void SetLanguage(string language)
    {
        CurrentCulture = CultureInfo.GetCultureInfo(language);
        LocalizationSource.Instance.Refresh();
    }

    public static string Get(string key, params object[] args)
    {
        var resourceKey = CurrentCulture.TwoLetterISOLanguageName.Equals("en", StringComparison.OrdinalIgnoreCase)
            ? $"{key}_EN"
            : key;
        var value = ResourceManager.GetString(resourceKey, CultureInfo.InvariantCulture) ?? key;
        return args.Length == 0 ? value : string.Format(CurrentCulture, value, args);
    }
}

public sealed class LocalizationSource : INotifyPropertyChanged
{
    public static LocalizationSource Instance { get; } = new();

    public string this[string key] => Localization.Get(key);

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Refresh()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
}

public sealed class LocExtension : MarkupExtension
{
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        return new Binding($"[{Key}]")
        {
            Source = LocalizationSource.Instance
        }.ProvideValue(serviceProvider);
    }
}
