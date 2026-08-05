using System.Windows;
using System.Windows.Media;
using ApplicationUpdater.Themes;
using Microsoft.Win32;

namespace ApplicationUpdater.Helpers;

/// <summary>
/// Applies Chronolog-style era themes (plus System) by replacing DynamicResource brushes.
/// Default preference is System (Windows light/dark app mode → Win11 / Win11 Dark).
/// </summary>
public static class ThemeManager
{
    private static string _preferenceId = ThemeCatalog.SystemId;
    private static bool _watching;
    private static ResourceDictionary? _activeThemeDict;
    private static ThemeDefinition _current = ThemeCatalog.Find("win11")!;

    public static string PreferenceId => _preferenceId;
    public static ThemeDefinition Current => _current;
    public static bool IsDarkEffective => _current.IsDarkSurface;

    public static event EventHandler? ThemeChanged;

    public static void Apply(string? preferenceName)
    {
        _preferenceId = ThemeCatalog.NormalizeId(preferenceName);
        ApplyEffective();
        EnsureSystemThemeWatch();
    }

    public static string ToConfigValue(string themeId) => ThemeCatalog.NormalizeId(themeId);

    /// <summary>Legacy enum helpers for older call sites.</summary>
    public static void Apply(AppTheme legacy)
    {
        Apply(legacy switch
        {
            AppTheme.Light => "win10",
            AppTheme.Dark => "win11-dark",
            _ => ThemeCatalog.SystemId
        });
    }

    public static AppTheme Parse(string? value)
    {
        var id = ThemeCatalog.NormalizeId(value);
        return id switch
        {
            "win11-dark" => AppTheme.Dark,
            ThemeCatalog.SystemId => AppTheme.System,
            _ => AppTheme.Light
        };
    }

    private static void ApplyEffective()
    {
        var app = Application.Current;
        if (app is null) return;

        _current = ResolveDefinition(_preferenceId);
        var themeDict = BuildThemeDictionary(_current);

        if (_activeThemeDict is not null)
            app.Resources.MergedDictionaries.Remove(_activeThemeDict);

        for (var i = app.Resources.MergedDictionaries.Count - 1; i >= 0; i--)
        {
            var md = app.Resources.MergedDictionaries[i];
            var src = md.Source?.OriginalString ?? string.Empty;
            if (src.Contains("ThemeBrushes", StringComparison.OrdinalIgnoreCase))
                app.Resources.MergedDictionaries.RemoveAt(i);
        }

        app.Resources.MergedDictionaries.Insert(0, themeDict);
        _activeThemeDict = themeDict;

        foreach (var key in themeDict.Keys)
            app.Resources[key] = themeDict[key];

        // Font for the app
        try
        {
            app.Resources["AppFont"] = new FontFamily(_current.UiFont);
        }
        catch
        {
            app.Resources["AppFont"] = new FontFamily("Segoe UI");
        }

        foreach (Window w in app.Windows)
            RefreshWindow(w);

        WindowChromeHelper.ApplyToAllOpenWindows(_current.IsDarkSurface);
        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    public static void RefreshWindow(Window w)
    {
        if (Application.Current?.Resources is null) return;

        if (Application.Current.Resources["WindowBackgroundBrush"] is Brush bg)
            w.Background = bg;
        if (Application.Current.Resources["PrimaryTextBrush"] is Brush fg)
            w.Foreground = fg;

        w.InvalidateProperty(Window.BackgroundProperty);
        w.InvalidateProperty(Window.ForegroundProperty);
        w.UpdateLayout();

        WindowChromeHelper.ApplyTheme(w, IsDarkEffective);
    }

    private static ThemeDefinition ResolveDefinition(string preferenceId)
    {
        if (preferenceId == ThemeCatalog.SystemId)
        {
            // System → modern Win11 light or dark based on OS app theme
            var id = IsWindowsLightTheme() ? "win11" : "win11-dark";
            return ThemeCatalog.Find(id) ?? ThemeCatalog.All[^1];
        }

        return ThemeCatalog.Find(preferenceId)
               ?? ThemeCatalog.Find("win11")
               ?? ThemeCatalog.All[0];
    }

    private static ResourceDictionary BuildThemeDictionary(ThemeDefinition t)
    {
        var d = new ResourceDictionary();

        void set(string key, Color color)
        {
            var brush = new SolidColorBrush(color);
            if (brush.CanFreeze) brush.Freeze();
            d[key] = brush;
        }

        Color Blend(Color a, Color b, double amountB)
        {
            amountB = Math.Clamp(amountB, 0, 1);
            return Color.FromRgb(
                (byte)(a.R + (b.R - a.R) * amountB),
                (byte)(a.G + (b.G - a.G) * amountB),
                (byte)(a.B + (b.B - a.B) * amountB));
        }

        // Surfaces
        set("WindowBackgroundBrush", t.Face);
        set("PanelBackgroundBrush", t.Window);
        set("PanelElevatedBrush", t.Parchment);
        set("BorderBrushSubtle", t.Border);
        set("StatusBarBrush", t.StatusBar);
        set("BusyOverlayBrush", Color.FromArgb(0xD0, t.Face.R, t.Face.G, t.Face.B));

        // Text
        set("PrimaryTextBrush", t.Ink);
        set("MutedTextBrush", t.MetaText);
        set("DisabledTextBrush", t.DisabledText);

        // Accent / interactive — use Highlight as primary action color when PrimaryButton is face-gray
        var accent = t.Highlight;
        // Classic themes: PrimaryButton is gray face; Highlight is navy selection
        set("AccentBrush", accent);
        set("AccentHoverBrush", Blend(accent, t.DarkShadow, 0.15));
        set("AccentPressedBrush", Blend(accent, t.DarkShadow, 0.30));

        // Text on accent buttons: HighlightText, except when accent is very light
        var onAccent = t.HighlightText;
        if (t.Id is "win11-dark")
            onAccent = t.HighlightText; // black on cyan
        set("OnAccentTextBrush", onAccent);

        // Lists / menus
        set("ListAltRowBrush", Blend(t.Window, t.Face, 0.35));
        set("ListSelectionBrush", t.Id.Contains("dark", StringComparison.OrdinalIgnoreCase)
            ? Blend(t.Highlight, t.Face, 0.55)
            : Blend(t.Highlight, Colors.White, 0.75));
        set("ListHoverBrush", t.Hover);
        set("MenuBackgroundBrush", t.MenuBar);
        set("MenuItemHighlightBrush", t.Highlight);
        set("MenuItemHighlightTextBrush", t.HighlightText);
        set("MenuSeparatorBrush", t.Shadow);

        // Controls
        set("ControlBackgroundBrush", t.Window);
        set("ControlHoverBrush", t.Hover);
        set("ControlPressedBrush", Blend(t.Hover, t.Shadow, 0.35));
        set("HeaderBackgroundBrush", t.Face);
        set("ScrollBarBrush", t.Shadow);
        set("ScrollBarHoverBrush", t.DarkShadow);

        // Semantic
        set("UpdateAvailableBrush", t.Highlight);
        set("UpdateOkBrush", t.MetaText);

        return d;
    }

    private static bool IsWindowsLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            if (value is int i)
                return i != 0;
        }
        catch
        {
            // ignore
        }

        return true;
    }

    private static void EnsureSystemThemeWatch()
    {
        if (_watching) return;
        _watching = true;
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle
                or UserPreferenceCategory.Color)
            {
                if (_preferenceId == ThemeCatalog.SystemId)
                    Application.Current?.Dispatcher.Invoke(ApplyEffective);
            }
        };
    }
}

/// <summary>Kept for compatibility with older call sites.</summary>
public enum AppTheme
{
    System,
    Light,
    Dark
}
