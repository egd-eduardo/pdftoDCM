using System.Collections.ObjectModel;
using System.Windows;

namespace PdfToDicom.App.Infrastructure;

/// <summary>
/// Swaps the colour resource dictionary between the light and dark schemes.
/// </summary>
/// <remarks>
/// Only the colour dictionary is exchanged. Control templates live in a separate
/// dictionary and reference the colours by key, so a theme change re-colours the whole
/// interface without rebuilding a single template.
/// </remarks>
public static class ThemeManager
{
    private const string LightThemeUri = "Themes/Light.xaml";
    private const string DarkThemeUri = "Themes/Dark.xaml";

    /// <summary>True when the dark scheme is currently applied.</summary>
    public static bool IsDarkMode { get; private set; }

    /// <summary>Applies the requested colour scheme to the running application.</summary>
    public static void Apply(bool darkMode)
    {
        Application? application = Application.Current;
        if (application is null)
        {
            return;
        }

        var newTheme = new ResourceDictionary
        {
            Source = new Uri(darkMode ? DarkThemeUri : LightThemeUri, UriKind.Relative)
        };

        // The colour dictionary is always the first merged dictionary, established in
        // App.xaml. Replacing it in place preserves the order that the templates rely on.
        Collection<ResourceDictionary> dictionaries = application.Resources.MergedDictionaries;

        if (dictionaries.Count == 0)
        {
            dictionaries.Add(newTheme);
        }
        else
        {
            dictionaries[0] = newTheme;
        }

        IsDarkMode = darkMode;
    }
}
