using System.Windows.Media;

namespace ApplicationUpdater.Themes;

/// <summary>
/// Theme list ported from Chronolog (Windows 95 → Windows 11 Dark),
/// plus a virtual "system" id resolved at runtime by ThemeManager.
/// </summary>
public static class ThemeCatalog
{
    public const string SystemId = "system";

    public static IReadOnlyList<ThemeDefinition> All { get; } =
    [
        // ── Windows 95 ──────────────────────────────────────────────
        new ThemeDefinition
        {
            Id = "win95",
            DisplayName = "Windows 95",
            Era = "1995",
            Face = Rgb(192, 192, 192),
            Window = Rgb(255, 255, 255),
            Parchment = Rgb(255, 255, 255),
            Ink = Rgb(0, 0, 0),
            Highlight = Rgb(0, 0, 128),
            HighlightText = Rgb(255, 255, 255),
            Shadow = Rgb(128, 128, 128),
            DarkShadow = Rgb(0, 0, 0),
            Light = Rgb(255, 255, 255),
            Accent = Rgb(192, 192, 192),
            AccentBorder = Rgb(128, 128, 128),
            Sidebar = Rgb(192, 192, 192),
            DisabledText = Rgb(128, 128, 128),
            MenuBar = Rgb(192, 192, 192),
            MenuBarText = Rgb(0, 0, 0),
            Hover = Rgb(224, 224, 224),
            TabInactive = Rgb(168, 168, 168),
            PrimaryButton = Rgb(192, 192, 192),
            Border = Rgb(0, 0, 0),
            StatusBar = Rgb(192, 192, 192),
            Placeholder = Rgb(128, 128, 128),
            MetaText = Rgb(64, 64, 64),
            UiFont = "MS Sans Serif, Tahoma, Segoe UI, sans-serif",
            UiFontSize = 11,
            CornerRadius = 0,
            ButtonCornerRadius = 0
        },

        // ── Windows 98 ──────────────────────────────────────────────
        new ThemeDefinition
        {
            Id = "win98",
            DisplayName = "Windows 98",
            Era = "1998",
            Face = Rgb(192, 192, 192),
            Window = Rgb(255, 255, 255),
            Parchment = Rgb(255, 252, 240),
            Ink = Rgb(0, 0, 0),
            Highlight = Rgb(0, 0, 128),
            HighlightText = Rgb(255, 255, 255),
            Shadow = Rgb(128, 128, 128),
            DarkShadow = Rgb(64, 64, 64),
            Light = Rgb(255, 255, 255),
            Accent = Rgb(212, 208, 200),
            AccentBorder = Rgb(128, 128, 128),
            Sidebar = Rgb(212, 208, 200),
            DisabledText = Rgb(128, 128, 128),
            MenuBar = Rgb(192, 192, 192),
            MenuBarText = Rgb(0, 0, 0),
            Hover = Rgb(224, 224, 224),
            TabInactive = Rgb(176, 176, 176),
            PrimaryButton = Rgb(212, 208, 200),
            Border = Rgb(0, 0, 0),
            StatusBar = Rgb(192, 192, 192),
            Placeholder = Rgb(128, 128, 128),
            MetaText = Rgb(80, 80, 80),
            UiFont = "MS Sans Serif, Tahoma, Segoe UI, sans-serif",
            UiFontSize = 11
        },

        // ── Windows 2000 ────────────────────────────────────────────
        new ThemeDefinition
        {
            Id = "win2000",
            DisplayName = "Windows 2000",
            Era = "2000",
            Face = Rgb(212, 208, 200),
            Window = Rgb(255, 255, 255),
            Parchment = Rgb(255, 248, 231),
            Ink = Rgb(0, 0, 0),
            Highlight = Rgb(10, 36, 106),
            HighlightText = Rgb(255, 255, 255),
            Shadow = Rgb(128, 128, 128),
            DarkShadow = Rgb(64, 64, 64),
            Light = Rgb(255, 255, 255),
            Accent = Rgb(245, 230, 200),
            AccentBorder = Rgb(168, 152, 120),
            Sidebar = Rgb(212, 208, 200),
            DisabledText = Rgb(128, 128, 128),
            MenuBar = Rgb(212, 208, 200),
            MenuBarText = Rgb(0, 0, 0),
            Hover = Rgb(232, 228, 216),
            TabInactive = Rgb(180, 176, 168),
            PrimaryButton = Rgb(212, 196, 168),
            Border = Rgb(64, 64, 64),
            StatusBar = Rgb(212, 208, 200),
            Placeholder = Rgb(128, 128, 128),
            MetaText = Rgb(85, 85, 85),
            UiFont = "Tahoma, Segoe UI, sans-serif",
            UiFontSize = 11
        },

        // ── Windows XP (Luna Blue) ──────────────────────────────────
        new ThemeDefinition
        {
            Id = "winxp",
            DisplayName = "Windows XP",
            Era = "2001",
            Face = Rgb(236, 233, 216),
            Window = Rgb(255, 255, 255),
            Parchment = Rgb(255, 255, 245),
            Ink = Rgb(0, 0, 0),
            Highlight = Rgb(49, 106, 197),
            HighlightText = Rgb(255, 255, 255),
            Shadow = Rgb(172, 168, 153),
            DarkShadow = Rgb(113, 111, 100),
            Light = Rgb(255, 255, 255),
            Accent = Rgb(255, 255, 225),
            AccentBorder = Rgb(0, 60, 116),
            Sidebar = Rgb(127, 157, 185),
            DisabledText = Rgb(161, 161, 146),
            MenuBar = Rgb(236, 233, 216),
            MenuBarText = Rgb(0, 0, 0),
            Hover = Rgb(196, 216, 240),
            TabInactive = Rgb(200, 198, 182),
            PrimaryButton = Rgb(236, 233, 216),
            Border = Rgb(0, 60, 116),
            StatusBar = Rgb(236, 233, 216),
            Placeholder = Rgb(128, 128, 128),
            MetaText = Rgb(70, 70, 90),
            UiFont = "Tahoma, Segoe UI, sans-serif",
            UiFontSize = 11,
            CornerRadius = 3,
            ButtonCornerRadius = 3
        },

        // ── Windows Vista ───────────────────────────────────────────
        new ThemeDefinition
        {
            Id = "winvista",
            DisplayName = "Windows Vista",
            Era = "2007",
            Face = Rgb(235, 240, 245),
            Window = Rgb(255, 255, 255),
            Parchment = Rgb(252, 252, 255),
            Ink = Rgb(30, 30, 30),
            Highlight = Rgb(51, 153, 255),
            HighlightText = Rgb(255, 255, 255),
            Shadow = Rgb(160, 175, 190),
            DarkShadow = Rgb(100, 120, 140),
            Light = Rgb(255, 255, 255),
            Accent = Rgb(230, 240, 250),
            AccentBorder = Rgb(120, 160, 200),
            Sidebar = Rgb(220, 230, 240),
            DisabledText = Rgb(140, 150, 160),
            MenuBar = Rgb(235, 240, 245),
            MenuBarText = Rgb(30, 30, 30),
            Hover = Rgb(190, 220, 250),
            TabInactive = Rgb(210, 220, 230),
            PrimaryButton = Rgb(200, 220, 240),
            Border = Rgb(140, 160, 180),
            StatusBar = Rgb(235, 240, 245),
            Placeholder = Rgb(140, 150, 160),
            MetaText = Rgb(80, 100, 120),
            UiFont = "Segoe UI, Tahoma, sans-serif",
            UiFontSize = 12,
            CornerRadius = 4,
            ButtonCornerRadius = 3
        },

        // ── Windows 7 ───────────────────────────────────────────────
        new ThemeDefinition
        {
            Id = "win7",
            DisplayName = "Windows 7",
            Era = "2009",
            Face = Rgb(240, 240, 240),
            Window = Rgb(255, 255, 255),
            Parchment = Rgb(255, 253, 245),
            Ink = Rgb(0, 0, 0),
            Highlight = Rgb(51, 153, 255),
            HighlightText = Rgb(255, 255, 255),
            Shadow = Rgb(160, 160, 160),
            DarkShadow = Rgb(105, 105, 105),
            Light = Rgb(255, 255, 255),
            Accent = Rgb(255, 250, 235),
            AccentBorder = Rgb(180, 170, 140),
            Sidebar = Rgb(230, 235, 240),
            DisabledText = Rgb(131, 131, 131),
            MenuBar = Rgb(240, 240, 240),
            MenuBarText = Rgb(0, 0, 0),
            Hover = Rgb(229, 241, 251),
            TabInactive = Rgb(220, 220, 220),
            PrimaryButton = Rgb(225, 235, 245),
            Border = Rgb(112, 112, 112),
            StatusBar = Rgb(240, 240, 240),
            Placeholder = Rgb(128, 128, 128),
            MetaText = Rgb(80, 80, 80),
            UiFont = "Segoe UI, Tahoma, sans-serif",
            UiFontSize = 12,
            CornerRadius = 3,
            ButtonCornerRadius = 2
        },

        // ── Windows 8 ───────────────────────────────────────────────
        new ThemeDefinition
        {
            Id = "win8",
            DisplayName = "Windows 8",
            Era = "2012",
            Face = Rgb(240, 240, 240),
            Window = Rgb(255, 255, 255),
            Parchment = Rgb(255, 255, 255),
            Ink = Rgb(0, 0, 0),
            Highlight = Rgb(0, 114, 198),
            HighlightText = Rgb(255, 255, 255),
            Shadow = Rgb(171, 171, 171),
            DarkShadow = Rgb(85, 85, 85),
            Light = Rgb(255, 255, 255),
            Accent = Rgb(240, 240, 240),
            AccentBorder = Rgb(0, 114, 198),
            Sidebar = Rgb(245, 245, 245),
            DisabledText = Rgb(153, 153, 153),
            MenuBar = Rgb(255, 255, 255),
            MenuBarText = Rgb(0, 0, 0),
            Hover = Rgb(222, 236, 249),
            TabInactive = Rgb(230, 230, 230),
            PrimaryButton = Rgb(0, 114, 198),
            Border = Rgb(171, 171, 171),
            StatusBar = Rgb(240, 240, 240),
            Placeholder = Rgb(153, 153, 153),
            MetaText = Rgb(102, 102, 102),
            UiFont = "Segoe UI, sans-serif",
            UiFontSize = 12
        },

        // ── Windows 10 ──────────────────────────────────────────────
        new ThemeDefinition
        {
            Id = "win10",
            DisplayName = "Windows 10",
            Era = "2015",
            Face = Rgb(243, 243, 243),
            Window = Rgb(255, 255, 255),
            Parchment = Rgb(255, 255, 255),
            Ink = Rgb(32, 32, 32),
            Highlight = Rgb(0, 120, 215),
            HighlightText = Rgb(255, 255, 255),
            Shadow = Rgb(200, 200, 200),
            DarkShadow = Rgb(96, 96, 96),
            Light = Rgb(255, 255, 255),
            Accent = Rgb(243, 243, 243),
            AccentBorder = Rgb(0, 120, 215),
            Sidebar = Rgb(249, 249, 249),
            DisabledText = Rgb(150, 150, 150),
            MenuBar = Rgb(243, 243, 243),
            MenuBarText = Rgb(32, 32, 32),
            Hover = Rgb(230, 240, 250),
            TabInactive = Rgb(230, 230, 230),
            PrimaryButton = Rgb(0, 120, 215),
            Border = Rgb(200, 200, 200),
            StatusBar = Rgb(243, 243, 243),
            Placeholder = Rgb(150, 150, 150),
            MetaText = Rgb(96, 96, 96),
            UiFont = "Segoe UI, sans-serif",
            UiFontSize = 12,
            CornerRadius = 2,
            ButtonCornerRadius = 2
        },

        // ── Windows 11 Light ────────────────────────────────────────
        new ThemeDefinition
        {
            Id = "win11",
            DisplayName = "Windows 11",
            Era = "2021",
            Face = Rgb(243, 243, 243),
            Window = Rgb(255, 255, 255),
            Parchment = Rgb(252, 252, 252),
            Ink = Rgb(28, 28, 28),
            Highlight = Rgb(0, 103, 192),
            HighlightText = Rgb(255, 255, 255),
            Shadow = Rgb(229, 229, 229),
            DarkShadow = Rgb(80, 80, 80),
            Light = Rgb(255, 255, 255),
            Accent = Rgb(243, 243, 243),
            AccentBorder = Rgb(0, 103, 192),
            Sidebar = Rgb(249, 249, 249),
            DisabledText = Rgb(150, 150, 150),
            MenuBar = Rgb(243, 243, 243),
            MenuBarText = Rgb(28, 28, 28),
            Hover = Rgb(237, 237, 237),
            TabInactive = Rgb(237, 237, 237),
            PrimaryButton = Rgb(0, 103, 192),
            Border = Rgb(229, 229, 229),
            StatusBar = Rgb(249, 249, 249),
            Placeholder = Rgb(150, 150, 150),
            MetaText = Rgb(96, 96, 96),
            UiFont = "Segoe UI Variable, Segoe UI, sans-serif",
            UiFontSize = 13,
            CornerRadius = 8,
            ButtonCornerRadius = 6
        },

        // ── Windows 11 Dark ─────────────────────────────────────────
        new ThemeDefinition
        {
            Id = "win11-dark",
            DisplayName = "Windows 11 Dark",
            Era = "2021",
            Face = Rgb(32, 32, 32),
            Window = Rgb(40, 40, 40),
            Parchment = Rgb(45, 45, 45),
            Ink = Rgb(255, 255, 255),
            Highlight = Rgb(96, 205, 255),
            HighlightText = Rgb(0, 0, 0),
            Shadow = Rgb(20, 20, 20),
            DarkShadow = Rgb(0, 0, 0),
            Light = Rgb(60, 60, 60),
            Accent = Rgb(50, 50, 50),
            AccentBorder = Rgb(96, 205, 255),
            Sidebar = Rgb(28, 28, 28),
            DisabledText = Rgb(128, 128, 128),
            MenuBar = Rgb(32, 32, 32),
            MenuBarText = Rgb(255, 255, 255),
            Hover = Rgb(55, 55, 55),
            TabInactive = Rgb(45, 45, 45),
            PrimaryButton = Rgb(96, 205, 255),
            Border = Rgb(60, 60, 60),
            StatusBar = Rgb(28, 28, 28),
            Placeholder = Rgb(140, 140, 140),
            MetaText = Rgb(180, 180, 180),
            UiFont = "Segoe UI Variable, Segoe UI, sans-serif",
            UiFontSize = 13,
            CornerRadius = 8,
            ButtonCornerRadius = 6
        }
    ];

    /// <summary>Picker rows: System first, then Chronolog era themes.</summary>
    public static IReadOnlyList<ThemeOption> PickerOptions { get; } =
        new List<ThemeOption>
        {
            new(SystemId, "System (match Windows)", "Follows light/dark app mode")
        }
        .Concat(All.Select(t => new ThemeOption(t.Id, t.DisplayName, t.Era)))
        .ToList();

    public static ThemeDefinition? Find(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Map legacy Light/Dark/System and unknown ids.</summary>
    public static string NormalizeId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return SystemId;

        var t = id.Trim();
        if (t.Equals("System", StringComparison.OrdinalIgnoreCase) ||
            t.Equals(SystemId, StringComparison.OrdinalIgnoreCase))
            return SystemId;
        if (t.Equals("Light", StringComparison.OrdinalIgnoreCase))
            return "win10";
        if (t.Equals("Dark", StringComparison.OrdinalIgnoreCase))
            return "win11-dark";

        var found = Find(t);
        return found?.Id ?? SystemId;
    }

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
}

public sealed record ThemeOption(string Id, string DisplayName, string Era)
{
    public string Label =>
        string.IsNullOrWhiteSpace(Era) || Id == ThemeCatalog.SystemId
            ? DisplayName
            : $"{DisplayName}  ({Era})";

    public override string ToString() => Label;
}
