using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

namespace Lockpick.App.Views;

/// <summary>
/// Hosts the overlay widgets without ever being shown (a toolkit window over exclusive fullscreen ended up below the game;
/// docs/DISPLAY.md): <see cref="Render"/> draws the panel to premultiplied BGRA pixels for
/// <see cref="Platform.Windows.LayeredWindow"/>, which shows them over the game.
/// </summary>
public partial class OverlayWindow : Window
{
    public OverlayWindow() => InitializeComponent();

    /// <summary>
    /// Renders <paramref name="panel"/> (<see cref="StatusPanel"/>, <see cref="MinimapPanel"/>) at
    /// <paramref name="size"/> (1 = designed for a 480-pixel-high picture) for a monitor at <paramref name="dpi"/>;
    /// returns its pixels and size in physical pixels.
    /// </summary>
    public static (byte[] Pixels, int Width, int Height) Render(LayoutTransformControl panel, double size, int dpi)
    {
        var scale = Math.Max(1, dpi) / 96.0;
        panel.LayoutTransform = new Avalonia.Media.ScaleTransform(size, size);
        // No layout pass runs in a window that is never shown, and a parent measured with the same space skips its
        // children: invalidate the whole panel so a changed size inside (e.g. the minimap's frame) is measured again.
        foreach (var control in panel.GetSelfAndVisualDescendants().OfType<Layoutable>())
        {
            control.InvalidateMeasure();
            control.InvalidateArrange();
        }

        panel.Measure(Size.Infinity);
        panel.Arrange(new Rect(panel.DesiredSize));
        var pixelSize = new PixelSize(
            Math.Max(1, (int)Math.Ceiling(panel.DesiredSize.Width * scale)),
            Math.Max(1, (int)Math.Ceiling(panel.DesiredSize.Height * scale)));
        using var bitmap = new RenderTargetBitmap(pixelSize, new Vector(96 * scale, 96 * scale));
        bitmap.Render(panel);
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
