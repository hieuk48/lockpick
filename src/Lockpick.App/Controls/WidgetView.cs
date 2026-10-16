using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Lockpick.App.Views;

namespace Lockpick.App.Controls;

/// <summary>
/// One overlay widget shown anywhere (a pop-out window, a Stream page tile), drawn by <see cref="WidgetRenderer"/> as
/// large as fits: redrawn ten times a second at most, and only when the widget changed (every tick while LiveSplit's
/// timer runs). While shown it keeps the widget's data updating (e.g. the map with the overlay off).
/// </summary>
public sealed class WidgetView : Control, IDisposable
{
    public static readonly StyledProperty<string?> WidgetIdProperty = AvaloniaProperty.Register<WidgetView, string?>(nameof(WidgetId));
    public static readonly StyledProperty<IBrush?> EmptyBrushProperty = AvaloniaProperty.Register<WidgetView, IBrush?>(nameof(EmptyBrush));

    private DispatcherTimer? _timer;
    private WriteableBitmap? _bitmap;
    private int _version = -1;
    private (int Width, int Height) _box;
    private string? _watching;

    static WidgetView()
    {
        AffectsRender<WidgetView>(WidgetIdProperty, EmptyBrushProperty);
    }

    public string? WidgetId { get => GetValue(WidgetIdProperty); set => SetValue(WidgetIdProperty, value); }

    /// <summary>The colour of the "nothing to show yet" note.</summary>
    public IBrush? EmptyBrush { get => GetValue(EmptyBrushProperty); set => SetValue(EmptyBrushProperty, value); }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => Tick());
        _timer.Start();
        Watch(WidgetId);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Dispose();
    }

    /// <summary>Stops drawing and frees the picture (done when the control leaves the screen).</summary>
    public void Dispose()
    {
        _timer?.Stop();
        _timer = null;
        Watch(null);
        _bitmap?.Dispose();
        _bitmap = null;
        _version = -1;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WidgetIdProperty && _timer is not null)
        {
            Watch(WidgetId);
            _version = -1;
        }
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (_bitmap is { } bitmap)
        {
            var scaling = Scaling();
            var size = new Size(bitmap.PixelSize.Width / scaling, bitmap.PixelSize.Height / scaling);
            var at = new Point((Bounds.Width - size.Width) / 2, (Bounds.Height - size.Height) / 2);
            context.DrawImage(bitmap, new Rect(at, size));
        }
        else if (EmptyBrush is { } brush)
        {
            var text = new FormattedText("Nothing to show yet", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 12, brush);
            context.DrawText(text, new Point((Bounds.Width - text.Width) / 2, (Bounds.Height - text.Height) / 2));
        }
    }

    private void Watch(string? id)
    {
        if (WidgetRenderer.Current is not { } renderer || id == _watching)
        {
            return;
        }

        if (_watching is not null)
        {
            renderer.Watch(_watching, false);
        }

        _watching = id;
        if (id is not null)
        {
            renderer.Watch(id, true);
        }
    }

    private double Scaling() => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;

    private void Tick()
    {
        if (WidgetRenderer.Current is not { } renderer || WidgetId is not { } id || !IsEffectivelyVisible)
        {
            return;
        }

        if (!renderer.HasContent(id))
        {
            if (_bitmap is not null)
            {
                _bitmap.Dispose();
                _bitmap = null;
                _version = -1;
                InvalidateVisual();
            }

            return;
        }

        var scaling = Scaling();
        var box = ((int)(Bounds.Width * scaling), (int)(Bounds.Height * scaling));
        var version = renderer.Version(id);
        if (!renderer.Live(id) && version == _version && box == _box && _bitmap is not null)
        {
            return;
        }

        _version = version;
        _box = box;
        if (renderer.RenderToFit(id, box.Item1, box.Item2, (int)Math.Round(96 * scaling)) is not var (pixels, width, height))
        {
            return;
        }

        if (_bitmap is null || _bitmap.PixelSize.Width != width || _bitmap.PixelSize.Height != height)
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96 * scaling, 96 * scaling), PixelFormat.Bgra8888, AlphaFormat.Premul);
        }

        using (var frame = _bitmap.Lock())
        {
            for (var y = 0; y < height; y++)
            {
                Marshal.Copy(pixels, y * width * 4, frame.Address + (y * frame.RowBytes), width * 4);
            }
        }

        InvalidateVisual();
    }
}
