using System.Linq;
using System.Windows;

namespace Sho.App.Services;

public static class LocalizationService
{
    public const string DefaultLanguage = "uk";
    public static readonly string[] AvailableLanguages = { "uk", "en" };

    public static string CurrentLanguage { get; private set; } = DefaultLanguage;

    public static void Apply(string language)
    {
        if (!AvailableLanguages.Contains(language)) language = DefaultLanguage;
        CurrentLanguage = language;

        var newDict = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Shozilla;component/Resources/Strings.{language}.xaml", UriKind.Absolute)
        };

        var dicts = Application.Current.Resources.MergedDictionaries;
        var existing = dicts.FirstOrDefault(d =>
            d.Source != null && d.Source.OriginalString.Contains("/Resources/Strings."));
        if (existing != null) dicts.Remove(existing);
        dicts.Add(newDict);
    }
}
