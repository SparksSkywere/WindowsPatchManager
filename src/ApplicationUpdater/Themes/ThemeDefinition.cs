using System.Windows.Media;

namespace ApplicationUpdater.Themes;

/// <summary>Color/font palette for one UI theme (ported from Chronolog era themes).</summary>
public sealed class ThemeDefinition
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Era { get; init; } = "";

    public Color Face { get; init; }
    public Color Window { get; init; }
    public Color Parchment { get; init; }
    public Color Ink { get; init; }
    public Color Highlight { get; init; }
    public Color HighlightText { get; init; }
    public Color Shadow { get; init; }
    public Color DarkShadow { get; init; }
    public Color Light { get; init; }
    public Color Accent { get; init; }
    public Color AccentBorder { get; init; }
    public Color Sidebar { get; init; }
    public Color DisabledText { get; init; }
    public Color MenuBar { get; init; }
    public Color MenuBarText { get; init; }
    public Color Hover { get; init; }
    public Color TabInactive { get; init; }
    public Color PrimaryButton { get; init; }
    public Color Border { get; init; }
    public Color StatusBar { get; init; }
    public Color Placeholder { get; init; }
    public Color MetaText { get; init; }

    public string UiFont { get; init; } = "Segoe UI, sans-serif";
    public double UiFontSize { get; init; } = 12;
    public double CornerRadius { get; init; }
    public double ButtonCornerRadius { get; init; }

    /// <summary>True when face/chrome is a dark surface (for title-bar immersive dark mode).</summary>
    public bool IsDarkSurface =>
        (Face.R + Face.G + Face.B) / 3.0 < 128;
}
