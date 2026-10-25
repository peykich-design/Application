using System.Windows;

namespace PadMapper.App.Services;

/// <summary>
/// 画面の文言（日本語/英語）。文言は Resources/Strings.ja.xaml と Strings.en.xaml にあり、
/// XAML からは {DynamicResource キー}、コードからは Loc.T("キー") で使う。
/// </summary>
internal static class Loc
{
    private static ResourceDictionary? _current;
    public static string Language { get; private set; } = "ja";

    public static void Apply(string language)
    {
        Language = language == "en" ? "en" : "ja";
        var dict = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/PadMapper;component/Resources/Strings.{Language}.xaml", UriKind.Absolute),
        };
        var merged = Application.Current.Resources.MergedDictionaries;
        if (_current != null) merged.Remove(_current);
        merged.Add(dict);
        _current = dict;
    }

    public static string T(string key) =>
        Application.Current?.TryFindResource(key) as string ?? key;

    public static string F(string key, params object?[] args)
    {
        try
        {
            return string.Format(T(key), args);
        }
        catch (FormatException)
        {
            return T(key);
        }
    }
}
