using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
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
    /// <summary>The most rows the enemies widget lists; the rest are counted.</summary>
    private const int MaxEnemyRows = 6;

    public OverlayWindow() => InitializeComponent();

    /// <summary>Fills the enemies widget: a row per enemy with its name, HP and a bar.</summary>
    public void ShowEnemies(IReadOnlyList<ViewModels.EnemyRow> enemies)
    {
        EnemyList.Children.Clear();
        var muted = Brush("LpMutedBrush");
        foreach (var enemy in enemies.Take(MaxEnemyRows))
        {
            var color = new SolidColorBrush(enemy.Color);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), ColumnSpacing = 6 };
            row.Children.Add(new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Background = color, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
            var name = new TextBlock { Text = enemy.Name, FontSize = 10, Foreground = enemy.IsBoss ? color : Brush("LpTextBrush"), FontWeight = enemy.IsBoss ? FontWeight.SemiBold : FontWeight.Normal };
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            var hp = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
            hp.Children.Add(new TextBlock { Text = enemy.Hp.ToString(System.Globalization.CultureInfo.InvariantCulture), FontSize = 10, Foreground = muted, FontFamily = Font("LpMono") });
            hp.Children.Add(new Border
            {
                Width = 50,
                Height = 4,
                CornerRadius = new CornerRadius(1),
                Background = Brush("LpPanelAltBrush"),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Child = new Border { Width = Math.Round(50 * enemy.Fraction, 1), CornerRadius = new CornerRadius(1), Background = color, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left },
            });
            Grid.SetColumn(hp, 2);
            row.Children.Add(hp);
            EnemyList.Children.Add(row);
        }

        if (enemies.Count > MaxEnemyRows)
        {
            EnemyList.Children.Add(new TextBlock { Text = $"+{enemies.Count - MaxEnemyRows} more", FontSize = 9, Foreground = muted });
        }
    }

    private static IBrush Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush ? brush : Brushes.White;

    private static FontFamily Font(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is FontFamily font ? font : FontFamily.Default;

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
