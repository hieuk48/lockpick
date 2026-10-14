using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Lockpick.Core.Maps;
using Lockpick.Core.State;

namespace Lockpick.App.Controls;

/// <summary>
/// Draws a room map top-down (X right, Z up) scaled to fit: blocking collision (walls, furniture), soft zones, doors
/// with their target room, item pickups with their names, and the player as an arrow pointing the way they face.
/// Facing uses the game's angle units (4096 per turn; 0 = +X, 1024 = −Z).
/// </summary>
public sealed class RoomMapControl : Control
{
    public static readonly StyledProperty<RoomMap?> MapProperty = AvaloniaProperty.Register<RoomMapControl, RoomMap?>(nameof(Map));
    public static readonly StyledProperty<double> PlayerXProperty = AvaloniaProperty.Register<RoomMapControl, double>(nameof(PlayerX));
    public static readonly StyledProperty<double> PlayerZProperty = AvaloniaProperty.Register<RoomMapControl, double>(nameof(PlayerZ));
    public static readonly StyledProperty<double> PlayerAngleProperty = AvaloniaProperty.Register<RoomMapControl, double>(nameof(PlayerAngle));
    public static readonly StyledProperty<IReadOnlyList<PlacedItem>?> PlacedItemsProperty =
        AvaloniaProperty.Register<RoomMapControl, IReadOnlyList<PlacedItem>?>(nameof(PlacedItems));
    public static readonly StyledProperty<IReadOnlySet<MapItem>?> TakenItemsProperty =
        AvaloniaProperty.Register<RoomMapControl, IReadOnlySet<MapItem>?>(nameof(TakenItems));
    public static readonly StyledProperty<IReadOnlySet<MapSocket>?> FilledSocketsProperty =
        AvaloniaProperty.Register<RoomMapControl, IReadOnlySet<MapSocket>?>(nameof(FilledSockets));
    public static readonly StyledProperty<bool> HasPlayerProperty = AvaloniaProperty.Register<RoomMapControl, bool>(nameof(HasPlayer));
    public static readonly StyledProperty<IBrush?> WallBrushProperty = AvaloniaProperty.Register<RoomMapControl, IBrush?>(nameof(WallBrush));
    public static readonly StyledProperty<IBrush?> SoftBrushProperty = AvaloniaProperty.Register<RoomMapControl, IBrush?>(nameof(SoftBrush));
    public static readonly StyledProperty<IBrush?> DoorBrushProperty = AvaloniaProperty.Register<RoomMapControl, IBrush?>(nameof(DoorBrush));
    public static readonly StyledProperty<IBrush?> ItemBrushProperty = AvaloniaProperty.Register<RoomMapControl, IBrush?>(nameof(ItemBrush));
    public static readonly StyledProperty<IBrush?> PlayerBrushProperty = AvaloniaProperty.Register<RoomMapControl, IBrush?>(nameof(PlayerBrush));

    private const double Inset = 12;

    static RoomMapControl()
    {
        AffectsRender<RoomMapControl>(MapProperty, PlacedItemsProperty, TakenItemsProperty, FilledSocketsProperty, PlayerXProperty, PlayerZProperty, PlayerAngleProperty, HasPlayerProperty,
            WallBrushProperty, SoftBrushProperty, DoorBrushProperty, ItemBrushProperty, PlayerBrushProperty);
    }

    public RoomMap? Map { get => GetValue(MapProperty); set => SetValue(MapProperty, value); }

    public double PlayerX { get => GetValue(PlayerXProperty); set => SetValue(PlayerXProperty, value); }

    public double PlayerZ { get => GetValue(PlayerZProperty); set => SetValue(PlayerZProperty, value); }

    public double PlayerAngle { get => GetValue(PlayerAngleProperty); set => SetValue(PlayerAngleProperty, value); }

    public bool HasPlayer { get => GetValue(HasPlayerProperty); set => SetValue(HasPlayerProperty, value); }

    /// <summary>
    /// Items the game has placed right now; when set, only these are drawn as present and the room files' other items
    /// are drawn faintly as "later" (story-gated or already taken).
    /// </summary>
    public IReadOnlyList<PlacedItem>? PlacedItems { get => GetValue(PlacedItemsProperty); set => SetValue(PlacedItemsProperty, value); }

    /// <summary>Room-file items the player has already taken (labelled "taken" rather than "later").</summary>
    public IReadOnlySet<MapItem>? TakenItems { get => GetValue(TakenItemsProperty); set => SetValue(TakenItemsProperty, value); }

    /// <summary>Use spots whose item has been put in place (e.g. the gold emblem in its slot).</summary>
    public IReadOnlySet<MapSocket>? FilledSockets { get => GetValue(FilledSocketsProperty); set => SetValue(FilledSocketsProperty, value); }

    public IBrush? WallBrush { get => GetValue(WallBrushProperty); set => SetValue(WallBrushProperty, value); }

    public IBrush? SoftBrush { get => GetValue(SoftBrushProperty); set => SetValue(SoftBrushProperty, value); }

    public IBrush? DoorBrush { get => GetValue(DoorBrushProperty); set => SetValue(DoorBrushProperty, value); }

    public IBrush? ItemBrush { get => GetValue(ItemBrushProperty); set => SetValue(ItemBrushProperty, value); }

    public IBrush? PlayerBrush { get => GetValue(PlayerBrushProperty); set => SetValue(PlayerBrushProperty, value); }

    public override void Render(DrawingContext context)
    {
        if (Map is not { } map || Bounds.Width < 40 || Bounds.Height < 40)
        {
            return;
        }

        var (x0, z0, x1, z1) = map.Bounds;
        var scale = Math.Min((Bounds.Width - 2 * Inset) / Math.Max(1, x1 - x0), (Bounds.Height - 2 * Inset) / Math.Max(1, z1 - z0));
        var offsetX = (Bounds.Width - (x1 - x0) * scale) / 2;
        var offsetY = (Bounds.Height - (z1 - z0) * scale) / 2;
        Point P(double x, double z) => new(offsetX + (x - x0) * scale, offsetY + (z1 - z) * scale);
        Rect R(double ax, double az, double bx, double bz) => new(P(ax, bz), P(bx, az));

        foreach (var s in map.Shapes)
        {
            if (s.X1 - s.X0 >= 60000 || s.Z1 - s.Z0 >= 60000)
            {
                continue;
            }

            var brush = s.Blocks ? WallBrush : SoftBrush;
            var rect = R(s.X0, s.Z0, s.X1, s.Z1);
            if (s.Kind == MapShapeKind.Circle)
            {
                context.DrawEllipse(brush, null, rect.Center, rect.Width / 2, rect.Height / 2);
            }
            else
            {
                context.DrawRectangle(brush, null, rect);
            }
        }

        var doorPen = DoorBrush is null ? null : new Pen(DoorBrush, 2);
        foreach (var d in map.Doors)
        {
            var rect = R(d.X, d.Z, d.X + d.Width, d.Z + d.Depth);
            context.DrawRectangle(null, doorPen, rect);
            DrawLabel(context, d.Target, rect.Center, DoorBrush, 11);
        }

        var placed = PlacedItems;
        var filled = FilledSockets ?? new HashSet<MapSocket>();
        foreach (var item in map.Items)
        {
            var present = placed is null || placed.Any(p => p.ItemId == item.ItemId && p.X == item.X && p.Z == item.Z);
            var rect = R(item.X, item.Z, item.X + item.Width, item.Z + item.Depth);
            if (!present && filled.Any(f => f.X == item.X && f.Z == item.Z))
            {
                continue; // the spot now holds the item put in place there (drawn below)
            }

            if (present)
            {
                context.DrawRectangle(null, ItemBrush is null ? null : new Pen(ItemBrush, 1.5), rect);
                DrawLabel(context, item.Name, rect.TopLeft + new Vector(rect.Width / 2, -8), ItemBrush, 10);
            }
            else if (SoftBrush is not null)
            {
                // In the room files but not placed now: already taken, or not placed until a story event.
                var taken = TakenItems?.Contains(item) == true;
                context.DrawRectangle(null, new Pen(SoftBrush, 1, new DashStyle(taken ? [1, 4] : [3, 3], 0)), rect);
                DrawLabel(context, item.Name + (taken ? " (taken)" : " (later)"), rect.TopLeft + new Vector(rect.Width / 2, -8), SoftBrush, 9);
            }
        }

        foreach (var socket in filled.Where(f => !map.Items.Any(i => i.ItemId == f.ItemId && i.X == f.X && i.Z == f.Z
                     && (placed is null || placed.Any(p => p.ItemId == i.ItemId && p.X == i.X && p.Z == i.Z)))))
        {
            var rect = R(socket.X, socket.Z, socket.X + socket.Width, socket.Z + socket.Depth);
            context.DrawRectangle(null, ItemBrush is null ? null : new Pen(ItemBrush, 1.5, new DashStyle([6, 2], 0)), rect);
            DrawLabel(context, socket.Name + " (in place)", rect.TopLeft + new Vector(rect.Width / 2, -8), ItemBrush, 10);
        }

        if (placed is not null)
        {
            // Placed by the game but not in the room files at that spot (e.g. moved by a script): draw them too.
            foreach (var p in placed.Where(p => !map.Items.Any(i => i.ItemId == p.ItemId && i.X == p.X && i.Z == p.Z)))
            {
                var rect = R(p.X, p.Z, p.X + p.Width, p.Z + p.Depth);
                context.DrawRectangle(null, ItemBrush is null ? null : new Pen(ItemBrush, 1.5), rect);
                var name = map.Items.FirstOrDefault(i => i.ItemId == p.ItemId)?.Name ?? $"item {p.ItemId}";
                DrawLabel(context, name, rect.TopLeft + new Vector(rect.Width / 2, -8), ItemBrush, 10);
            }
        }

        if (HasPlayer && PlayerBrush is not null)
        {
            var at = P(PlayerX, PlayerZ);
            var theta = PlayerAngle * Math.PI * 2 / 4096;
            // World direction (cos θ, −sin θ) in X/Z; screen Y runs opposite to Z.
            var forward = new Vector(Math.Cos(theta), Math.Sin(theta));
            var side = new Vector(-forward.Y, forward.X);
            var size = 9.0;
            var tip = at + forward * size * 1.4;
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(tip, true);
                g.LineTo(at - forward * size * 0.6 + side * size * 0.8);
                g.LineTo(at - forward * size * 0.6 - side * size * 0.8);
                g.EndFigure(true);
            }

            context.DrawGeometry(PlayerBrush, null, geometry);
        }
    }

    private static void DrawLabel(DrawingContext context, string text, Point center, IBrush? brush, double size)
    {
        if (brush is null || string.IsNullOrEmpty(text))
        {
            return;
        }

        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, size, brush);
        context.DrawText(formatted, center - new Vector(formatted.Width / 2, formatted.Height / 2));
    }
}
