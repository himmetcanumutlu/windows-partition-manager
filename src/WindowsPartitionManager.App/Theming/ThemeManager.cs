using System.Windows;
using System.Windows.Media;

namespace WindowsPartitionManager.App.Theming;

/// <summary>
/// Swaps the active colour theme by replacing the theme <see cref="ResourceDictionary"/> in the
/// application's merged dictionaries, and applies the UI font by updating the shared
/// <c>UiFontFamily</c> resource. Both take effect immediately, without a restart.
/// </summary>
public static class ThemeManager
{
    private static readonly string[] Themes = ["White", "Dark", "Night"];

    private static ResourceDictionary? _currentTheme;

    public static void ApplyTheme(string themeName)
    {
        if (!Themes.Contains(themeName))
        {
            themeName = "White";
        }

        var assembly = typeof(ThemeManager).Assembly.GetName().Name;
        var uri = new Uri($"pack://application:,,,/{assembly};component/Themes/{themeName}.xaml", UriKind.Absolute);
        var dictionary = new ResourceDictionary { Source = uri };

        var merged = Application.Current!.Resources.MergedDictionaries;
        if (_currentTheme is not null)
        {
            merged.Remove(_currentTheme);
        }

        merged.Add(dictionary);
        _currentTheme = dictionary;
    }

    public static void ApplyFont(string fontName)
    {
        Application.Current!.Resources["UiFontFamily"] = new FontFamily(fontName);
    }
}
