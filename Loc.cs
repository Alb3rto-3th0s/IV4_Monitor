using System.Windows;

namespace VisorIV4;

/// <summary>
/// Localization. Texts live in Lang\en.xaml (default) and Lang\es.xaml.
/// XAML uses {DynamicResource S_Key} so switching language updates the UI live;
/// code uses Loc.T / Loc.F.
/// </summary>
public static class Loc
{
    public static readonly string[] Supported = { "en", "es" };
    private static ResourceDictionary? _current;

    public static string Current { get; private set; } = "en";

    public static event Action? Changed;

    public static string Normalize(string? lang) =>
        Supported.FirstOrDefault(s => s.Equals(lang?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? "en";

    public static void Apply(string? lang)
    {
        lang = Normalize(lang);
        var dict = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Lang/{lang}.xaml") };
        var merged = Application.Current.Resources.MergedDictionaries;
        if (_current is not null) merged.Remove(_current);
        merged.Add(dict);
        _current = dict;
        bool changed = Current != lang;
        Current = lang;
        if (changed) Changed?.Invoke();
    }

    public static string T(string key) => Application.Current.TryFindResource(key) as string ?? key;

    public static string F(string key, params object[] args) => string.Format(T(key), args);
}
