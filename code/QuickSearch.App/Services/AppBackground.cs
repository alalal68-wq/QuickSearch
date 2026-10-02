using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuickSearch.App.Services;

/// <summary>
/// Builds the app's panel background: the user's wallpaper with a dark translucent
/// layer on top, as a single brush (image + dim layer share the same bounds, so
/// they always crop together).
/// </summary>
public static class AppBackground
{
    /// <summary>Plain dark panel color used when no wallpaper is chosen.</summary>
    public static readonly System.Windows.Media.Brush DefaultPanel = CreateDefault();

    private static System.Windows.Media.Brush CreateDefault()
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xEE, 0x0F, 0x13, 0x1C));
        brush.Freeze();
        return brush;
    }

    /// <summary>Returns null if the file is missing or cannot be decoded.</summary>
    public static System.Windows.Media.Brush? TryCreate(string? path, int dimPercent)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
            return null;

        try
        {
            // The panel is a few hundred pixels wide: decoding a 4K+ photo at full size
            // only wastes startup time and memory, so cap the decoded width.
            const int MaxDecodeWidth = 2200;
            int sourceWidth;
            using (var probe = System.IO.File.OpenRead(path))
            {
                sourceWidth = BitmapFrame.Create(probe,
                    BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
                    BitmapCacheOption.None).PixelWidth; // reads the header only
            }

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            if (sourceWidth > MaxDecodeWidth)
                bitmap.DecodePixelWidth = MaxDecodeWidth;
            bitmap.CacheOption = BitmapCacheOption.OnLoad; // release the file right after loading
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();

            var bounds = new System.Windows.Rect(0, 0, bitmap.Width, bitmap.Height);
            var alpha = (byte)Math.Round(255 * Math.Clamp(dimPercent, 0, 100) / 100.0);
            var dim = new SolidColorBrush(System.Windows.Media.Color.FromArgb(alpha, 0x0A, 0x0E, 0x15));
            dim.Freeze();

            var group = new DrawingGroup();
            group.Children.Add(new ImageDrawing(bitmap, bounds));
            group.Children.Add(new GeometryDrawing(dim, null, new RectangleGeometry(bounds)));
            group.Freeze();

            var brush = new DrawingBrush(group)
            {
                Stretch = Stretch.UniformToFill,
                AlignmentX = AlignmentX.Center,
                AlignmentY = AlignmentY.Center
            };
            brush.Freeze();
            return brush;
        }
        catch
        {
            return null;
        }
    }
}
