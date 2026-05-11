using System;
using System.Windows;
using PowerDesk.Core.Models;
using Application = System.Windows.Application;

namespace PowerDesk.Core.Theming;

public sealed class ThemeService
{
    public event EventHandler<AppTheme>? ThemeChanged;

    public AppTheme Current { get; private set; } = AppTheme.Dark;

    public void Apply(AppTheme theme)
    {
        Current = theme;
        var src = theme switch
        {
            AppTheme.Light    => "Resources/Themes/Light.xaml",
            AppTheme.OledDark => "Resources/Themes/OledDark.xaml",
            _                 => "Resources/Themes/Dark.xaml",
        };
        var uri = new Uri(src, UriKind.Relative);
        var newDict = new ResourceDictionary { Source = uri };

        var app = Application.Current;
        if (app is null) return;

        // Replace the first theme dictionary (we place it at index 0 in App.xaml).
        if (app.Resources.MergedDictionaries.Count > 0)
            app.Resources.MergedDictionaries[0] = newDict;
        else
            app.Resources.MergedDictionaries.Add(newDict);

        ThemeChanged?.Invoke(this, theme);
    }
}
