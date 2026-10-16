using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace Lockpick.App.Views;

/// <summary>
/// Hosts the overlay panel without ever being shown (a toolkit window over exclusive fullscreen ended up below the game;
/// docs/DISPLAY.md): <see cref="Render"/> draws the panel to premultiplied BGRA pixels for
/// <see cref="Platform.Windows.LayeredWindow"/>, which shows them over the game.
/// </summary>
public partial class OverlayWindow : Window
{
    public OverlayWindow() => InitializeComponent();

    /// <summary>
    /// Renders the panel at <paramref name="size"/> (1 = designed for a 480-pixel-high picture) for a monitor at
    /// <paramref name="dpi"/>; returns its pixels and size in physical pixels.
    /// </summary>
    public (byte[] Pixels, int Width, int Height) Render(double size, int dpi)
    {
        var scale = Math.Max(1, dpi) / 96.0;
        Panel.LayoutTransform = new Avalonia.Media.ScaleTransform(size, size);
        Panel.Measure(Size.Infinity);
        Panel.Arrange(new Rect(Panel.DesiredSize));
        var pixelSize = new PixelSize(
            Math.Max(1, (int)Math.Ceiling(Panel.DesiredSize.Width * scale)),
            Math.Max(1, (int)Math.Ceiling(Panel.DesiredSize.Height * scale)));
        using var bitmap = new RenderTargetBitmap(pixelSize, new Vector(96 * scale, 96 * scale));
        bitmap.Render(Panel);
        var pixels = new byte[pixelSize.Width * pixelSize.Height * 4];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(pixelSize), handle.AddrOfPinnedObject(), pixels.Length, pixelSize.Width * 4);
        }
        finally
        {
            handle.Free();
        }

        return (pixels, pixelSize.Width, pixelSize.Height);
    }
}
