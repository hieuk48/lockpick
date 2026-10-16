using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Lockpick.App.Services;
using Lockpick.App.ViewModels;
using Lockpick.App.Views;

namespace Lockpick.App.Controls;

/// <summary>
/// The overlay's layout editor (Settings → Overlay, #55): the game's 4:3 picture in small, with each widget that is on
/// drawn where the overlay puts it (its real picture when it has one, a labelled box when it's empty for now). Drag a
/// widget to place it anywhere: it then keeps that spot (<see cref="OverlayCorners.Custom"/>) on any picture size.
/// </summary>
public sealed class OverlayLayoutEditor : Control
{
    public static readonly StyledProperty<IReadOnlyList<OverlayWidgetRow>?> RowsProperty =
        AvaloniaProperty.Register<OverlayLayoutEditor, IReadOnlyList<OverlayWidgetRow>?>(nameof(Rows));

    /// <summary>Widget sizes in the overlay's own units (a 480-pixel-high picture) while they have nothing to show.</summary>
    private static readonly Dictionary<string, (double Width, double Height)> EmptySizes = new()
    {
        [OverlayLayout.Status] = (150, 56),
        [OverlayLayout.Minimap] = (130, 130),
        [OverlayLayout.Enemies] = (140, 60),
        [OverlayLayout.Splits] = (170, 300),
        [OverlayLayout.Feed] = (220, 70),
        [OverlayLayout.Banner] = (300, 54),
    };

    private static readonly SolidColorBrush Picture = new SolidColorBrush(Color.Parse("#1A2420"));
    private static readonly SolidColorBrush Box = new SolidColorBrush(Color.Parse("#552E3A36"));
    private static readonly Pen BoxPen = new Pen(new SolidColorBrush(Color.Parse("#8FA39B")), 1, DashStyle.Dash);
    private static readonly Pen DragPen = new Pen(new SolidColorBrush(Color.Parse("#4EC36D")), 2);
    private static readonly SolidColorBrush Label = new SolidColorBrush(Color.Parse("#DFE8E3"));

    private readonly Dictionary<string, (WriteableBitmap Bitmap, int Version, double Scale)> _pictures = [];
    private readonly Dictionary<string, Rect> _rects = [];
    private DispatcherTimer? _timer;
    private string? _dragging;
    private Point? _dragPosition;
    private Point _grab;
    private Rect _picture;
    private double _scale;

    static OverlayLayoutEditor() => AffectsRender<OverlayLayoutEditor>(RowsProperty);

    public OverlayLayoutEditor()
    {
        Cursor = new Cursor(StandardCursorType.Hand);
        MinHeight = 200;
    }

    public IReadOnlyList<OverlayWidgetRow>? Rows { get => GetValue(RowsProperty); set => SetValue(RowsProperty, value); }

    /// <summary>A widget was dropped at a spot of its own.</summary>
    public event EventHandler<(string Id, WidgetSpot Spot)>? Placed;

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 640 : availableSize.Width;
        return new Size(width, Math.Min(width * 3 / 4, 540));
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => InvalidateVisual());
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer?.Stop();
        _timer = null;
        foreach (var picture in _pictures.Values)
        {
            picture.Bitmap.Dispose();
        }

        _pictures.Clear();
    }

    public override void Render(DrawingContext context)
    {
        // The 4:3 picture, as large as fits, centred.
        var height = Math.Min(Bounds.Height, Bounds.Width * 3 / 4);
        var width = height * 4 / 3;
        _picture = new Rect((Bounds.Width - width) / 2, (Bounds.Height - height) / 2, width, height);
        _scale = height / 480;
        context.FillRectangle(Picture, _picture, 3);
        Text(context, "The game's picture", new Point(_picture.X + 8, _picture.Bottom - 22), 11, BoxPen.Brush!);
        if (Rows is not { } rows || WidgetRenderer.Current is not { } renderer)
        {
            return;
        }

        var margin = OverlayViewModel.Margin * _scale;
        List<OverlayPlacement.Item> items = [];
        foreach (var row in rows.Where(r => r.Corner != OverlayLayout.Off))
        {
            var (w, h) = SizeOf(row.Id, renderer);
            var full = renderer.Full(row.Id);
            items.Add(new OverlayPlacement.Item(row.Id, row.Corner, row.Spot,
                full ? width - (2 * margin) : w, full ? height - (2 * margin) : h, full));
        }

        var places = OverlayPlacement.Place(items, _picture.X, _picture.Y, width, height, margin);
        _rects.Clear();
        foreach (var item in items)
        {
            var at = places[item.Id];
            if (_dragging == item.Id && _dragPosition is { } drag)
            {
                at = (drag.X, drag.Y);
            }

            var rect = new Rect(at.X, at.Y, item.Width, item.Height);
            _rects[item.Id] = rect;
            if (!item.Full && _pictures.TryGetValue(item.Id, out var picture) && renderer.HasContent(item.Id))
            {
                context.DrawImage(picture.Bitmap, rect);
            }
            else
            {
                context.DrawRectangle(Box, BoxPen, rect, 3, 3);
            }

            var name = OverlayLayout.Widgets.FirstOrDefault(w => w.Id == item.Id).Name ?? item.Id;
            if (item.Full || !renderer.HasContent(item.Id))
            {
                Text(context, item.Full ? $"{name} (full screen)" : name, rect.TopLeft + new Point(6, 4), 11, Label);
            }

            if (_dragging == item.Id)
            {
                context.DrawRectangle(null, DragPen, rect, 3, 3);
            }
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var at = e.GetPosition(this);
        // The topmost widget under the pointer (later ones are drawn over earlier ones); full-screen maps stay put.
        foreach (var (id, rect) in _rects.Reverse())
        {
            if (rect.Contains(at) && WidgetRenderer.Current?.Full(id) != true)
            {
                _dragging = id;
                _grab = at - rect.TopLeft;
                _dragPosition = rect.TopLeft;
                e.Pointer.Capture(this);
                InvalidateVisual();
                return;
            }
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging is null || !_rects.TryGetValue(_dragging, out var rect))
        {
            return;
        }

        var margin = OverlayViewModel.Margin * _scale;
        var at = e.GetPosition(this) - _grab;
        _dragPosition = new Point(
            Math.Clamp(at.X, _picture.X + margin, Math.Max(_picture.X + margin, _picture.Right - margin - rect.Width)),
            Math.Clamp(at.Y, _picture.Y + margin, Math.Max(_picture.Y + margin, _picture.Bottom - margin - rect.Height)));
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragging is { } id && _dragPosition is { } drop && _rects.TryGetValue(id, out var rect))
        {
            var spot = OverlayPlacement.SpotFor(drop.X, drop.Y, rect.Width, rect.Height, _picture.X, _picture.Y, _picture.Width, _picture.Height,
                OverlayViewModel.Margin * _scale);
            Placed?.Invoke(this, (id, spot));
        }

        _dragging = null;
        _dragPosition = null;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    /// <summary>The widget's size in the preview; its picture is drawn again when it changed.</summary>
    private (double Width, double Height) SizeOf(string id, WidgetRenderer renderer)
    {
        if (renderer.Full(id) || !renderer.HasContent(id))
        {
            var (w, h) = EmptySizes.GetValueOrDefault(id, (120, 60));
            return (w * _scale, h * _scale);
        }

        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var version = renderer.Version(id);
        if (!_pictures.TryGetValue(id, out var known) || known.Version != version || known.Scale != _scale || renderer.Live(id))
        {
            if (renderer.Render(id, _scale, (int)Math.Round(96 * scaling)) is var (pixels, width, height))
            {
                var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96 * scaling, 96 * scaling), PixelFormat.Bgra8888, AlphaFormat.Premul);
                using (var frame = bitmap.Lock())
                {
                    for (var y = 0; y < height; y++)
                    {
                        Marshal.Copy(pixels, y * width * 4, frame.Address + (y * frame.RowBytes), width * 4);
                    }
                }

                if (known.Bitmap is { } old)
                {
                    old.Dispose();
                }

                _pictures[id] = known = (bitmap, version, _scale);
            }
        }

        return known.Bitmap is { } shown ? (shown.PixelSize.Width / scaling, shown.PixelSize.Height / scaling) : EmptySizes.GetValueOrDefault(id, (120, 60));
    }

    private static void Text(DrawingContext context, string text, Point at, double size, IBrush brush) =>
        context.DrawText(new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, size, brush), at);
}
