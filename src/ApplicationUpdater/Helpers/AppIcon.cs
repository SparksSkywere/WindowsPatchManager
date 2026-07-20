using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ApplicationUpdater.Helpers;

/// <summary>Loads the production app icon for WPF windows and dialogs.</summary>
public static class AppIcon
{
    private static ImageSource? _cached;

    public static ImageSource? GetImageSource()
    {
        if (_cached is not null)
            return _cached;

        // 1) Embedded resource (preferred)
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute);
            var frame = BitmapFrame.Create(uri, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            frame.Freeze();
            _cached = frame;
            return _cached;
        }
        catch
        {
            // fall through
        }

        // 2) Loose file next to the EXE
        foreach (var path in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"),
                     Path.Combine(AppContext.BaseDirectory, "app.ico")
                 })
        {
            if (!File.Exists(path))
                continue;

            try
            {
                var frame = BitmapFrame.Create(
                    new Uri(path, UriKind.Absolute),
                    BitmapCreateOptions.None,
                    BitmapCacheOption.OnLoad);
                frame.Freeze();
                _cached = frame;
                return _cached;
            }
            catch
            {
                // try next
            }
        }

        return null;
    }

    public static void ApplyTo(Window window)
    {
        var icon = GetImageSource();
        if (icon is not null)
            window.Icon = icon;
    }
}
