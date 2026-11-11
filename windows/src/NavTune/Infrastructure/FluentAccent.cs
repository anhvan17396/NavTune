using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace NavTune.Infrastructure;

/// <summary>
/// Gives the WPF Fluent theme NavTune's purple instead of the Windows accent color.
///
/// The theme builds its brushes with the Windows accent shades baked in when it loads, so overriding the
/// SystemAccentColor keys has no effect. Instead, every theme brush whose color is one of the seven Windows accent
/// shades gets the matching NavTune shade (alpha kept). This runs again whenever Windows switches between light and
/// dark, because the theme is reloaded then.
/// </summary>
public static class FluentAccent
{
    private static readonly Color[] BrandShades =
    [
        Color.FromRgb(0x61, 0x5C, 0xFA), // accent
        Color.FromRgb(0x7C, 0x78, 0xFB), // light 1
        Color.FromRgb(0x9C, 0x99, 0xFC), // light 2
        Color.FromRgb(0xBE, 0xBC, 0xFD), // light 3
        Color.FromRgb(0x52, 0x4D, 0xEB), // dark 1
        Color.FromRgb(0x45, 0x40, 0xD2), // dark 2
        Color.FromRgb(0x35, 0x31, 0xA8), // dark 3
    ];

    private static ResourceDictionary? applied;

    public static void Attach(Application app)
    {
        Apply(app);
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
                app.Dispatcher.BeginInvoke(() => Apply(app), DispatcherPriority.Background);
        };
    }

    public static void Apply(Application app)
    {
        var theme = FindTheme(app.Resources);
        if (theme is null || ReferenceEquals(theme, applied)) return;
        Color[] system =
        [
            SystemColors.AccentColor, SystemColors.AccentColorLight1, SystemColors.AccentColorLight2, SystemColors.AccentColorLight3,
            SystemColors.AccentColorDark1, SystemColors.AccentColorDark2, SystemColors.AccentColorDark3,
        ];
        Recolor(theme, system);
        applied = theme;
    }

    private static ResourceDictionary? FindTheme(ResourceDictionary dictionary)
    {
        foreach (var merged in dictionary.MergedDictionaries)
        {
            if (merged.Source?.OriginalString.Contains("PresentationFramework.Fluent", StringComparison.OrdinalIgnoreCase) == true) return merged;
            if (FindTheme(merged) is { } nested) return nested;
        }
        return null;
    }

    private static void Recolor(ResourceDictionary dictionary, Color[] system)
    {
        foreach (var merged in dictionary.MergedDictionaries) Recolor(merged, system);
        foreach (var key in dictionary.Keys.Cast<object>().ToList())
        {
            object value;
            try { value = dictionary[key]; }
            catch { continue; }
            switch (value)
            {
                case SolidColorBrush brush when Map(brush.Color, system) is { } color:
                    if (brush.IsFrozen) dictionary[key] = new SolidColorBrush(color) { Opacity = brush.Opacity };
                    else brush.Color = color;
                    break;
                case LinearGradientBrush gradient when gradient.GradientStops.Any(s => Map(s.Color, system) is not null):
                    var copy = gradient.IsFrozen ? gradient.Clone() : gradient;
                    foreach (var stop in copy.GradientStops)
                        if (Map(stop.Color, system) is { } mapped) stop.Color = mapped;
                    if (!ReferenceEquals(copy, gradient)) dictionary[key] = copy;
                    break;
                case Color color when Map(color, system) is { } mappedColor:
                    dictionary[key] = mappedColor;
                    break;
            }
        }
    }

    private static Color? Map(Color color, Color[] system)
    {
        for (int i = 0; i < system.Length; i++)
        {
            var s = system[i];
            if (color.R == s.R && color.G == s.G && color.B == s.B)
                return Color.FromArgb(color.A, BrandShades[i].R, BrandShades[i].G, BrandShades[i].B);
        }
        return null;
    }
}
