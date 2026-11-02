using System.Collections.Generic;
using Microsoft.UI.Xaml;
using System.Diagnostics.CodeAnalysis;

namespace Station.Services;

/// <summary>
/// Resolves theme-dictionary resources (Colors.xaml Light/Dark) for code-behind.
/// Application.Current.Resources only honours the OS app theme, not the in-app
/// ThemeService choice, so we look the key up in the matching theme dictionary.
/// Values are resolved at call time: UI built in code picks up a theme change the
/// next time it is rebuilt.
/// </summary>
public static class ThemeBrushes
{
    public static bool TryGet(string key, [NotNullWhen(true)] out object? value)
    {
        var themeKey = ThemeService.Instance.CurrentTheme == ElementTheme.Light ? "Light" : "Dark";
        var root = Application.Current.Resources;

        if (TryFind(root, themeKey, key, out value))
        {
            return true;
        }

        return root.TryGetValue(key, out value);
    }

    public static object Get(string key)
    {
        if (TryGet(key, out var value))
        {
            return value;
        }

        throw new KeyNotFoundException($"Resource '{key}' not found.");
    }

    private static bool TryFind(ResourceDictionary dict, string themeKey, string key, out object? value)
    {
        if (dict.ThemeDictionaries.TryGetValue(themeKey, out var themed)
            && themed is ResourceDictionary themedDict
            && themedDict.TryGetValue(key, out value))
        {
            return true;
        }

        foreach (var merged in dict.MergedDictionaries)
        {
            if (TryFind(merged, themeKey, key, out value))
            {
                return true;
            }
        }

        value = null;
        return false;
    }
}
