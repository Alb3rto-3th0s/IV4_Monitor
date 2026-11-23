using System.Windows;

namespace VisorIV4;

/// <summary>Color theme. Brushes live in Themes\Dark.xaml (default) and Themes\Light.xaml;
/// XAML uses {DynamicResource ...Brush} so switching updates the UI live.</summary>
public static class Theme
{
    private static ResourceDictionary? _current;

    public static string Current { get; private set; } = "";

    public static string Normalize(string? theme) =>
        string.Equals(theme?.Trim(), "Light", StringComparison.OrdinalIgnoreCase) ? "Light" : "Dark";

    /// <returns>true if the theme changed.</returns>
    public static bool Apply(string? theme)
    {
        theme = Normalize(theme);
        if (theme == Current) return false;
        var dict = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Themes/{theme}.xaml") };
        var merged = Application.Current.Resources.MergedDictionaries;
        if (_current is not null) merged.Remove(_current);
        merged.Add(dict);
        _current = dict;
        Current = theme;
        return true;
    }
}
