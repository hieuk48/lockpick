namespace Lockpick.Core.Display;

/// <summary>A rectangle in physical screen pixels (what a per-monitor DPI aware process sees on every monitor).</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public override string ToString() => $"{Width}×{Height} at {X},{Y}";
}

/// <summary>How the game is on screen (docs/DISPLAY.md).</summary>
public enum GameWindowMode
{
    /// <summary>A window with a title bar.</summary>
    Windowed,

    /// <summary>A borderless window covering a monitor, composed by Windows like any window (overlays work).</summary>
    Borderless,

    /// <summary>A topmost window covering a monitor: exclusive fullscreen; it minimizes and stops on any focus change.</summary>
    ExclusiveFullscreen,
}

/// <summary>The game's window as Lockpick's overlay needs it.</summary>
/// <param name="Handle">The window handle.</param>
/// <param name="Client">The client area (the picture area, title bar excluded), physical pixels.</param>
/// <param name="Monitor">The monitor the window is on, physical pixels.</param>
/// <param name="Dpi">The window's DPI (96 = 100 %).</param>
public sealed record GameWindowInfo(
    nint Handle,
    PixelRect Client,
    PixelRect Monitor,
    int Dpi,
    bool Minimized,
    bool Visible,
    bool Foreground,
    GameWindowMode Mode)
{
    /// <summary>True when an overlay can be shown over the window.</summary>
    public bool CanOverlay => Visible && !Minimized && !Client.IsEmpty;

    /// <summary>Works out the mode from the window's style and where its client area sits.</summary>
    public static GameWindowMode Classify(bool hasCaption, bool topmost, PixelRect client, PixelRect monitor) =>
        hasCaption || client != monitor ? GameWindowMode.Windowed
        : topmost ? GameWindowMode.ExclusiveFullscreen
        : GameWindowMode.Borderless;
}

/// <summary>How the game scales its picture into its window.</summary>
public enum PictureScaling
{
    /// <summary>The picture fills the whole client area.</summary>
    Stretch,

    /// <summary>The largest picture with the game's own aspect ratio, centred (bars at the sides or top and bottom).</summary>
    Fit,

    /// <summary>The largest whole multiple of the game's own size, centred.</summary>
    WholeMultiple,
}

/// <summary>The game's display settings: how the picture sits in the window.</summary>
/// <param name="NativeWidth">The game's own picture width (RE1: 640).</param>
/// <param name="NativeHeight">The game's own picture height (RE1: 480).</param>
public sealed record GameDisplaySettings(PictureScaling Scaling, int NativeWidth, int NativeHeight)
{
    /// <summary>Where the game's picture is inside a client area of this size (same coordinates as the client).</summary>
    public PixelRect Picture(PixelRect client)
    {
        if (client.IsEmpty || Scaling == PictureScaling.Stretch || NativeWidth <= 0 || NativeHeight <= 0)
        {
            return client;
        }

        int width, height;
        var whole = Math.Min(client.Width / NativeWidth, client.Height / NativeHeight);
        if (Scaling == PictureScaling.WholeMultiple && whole >= 1)
        {
            width = NativeWidth * whole;
            height = NativeHeight * whole;
        }
        else
        {
            // Fit (and WholeMultiple in a window smaller than the game's own size).
            var scale = Math.Min((double)client.Width / NativeWidth, (double)client.Height / NativeHeight);
            width = (int)Math.Round(NativeWidth * scale);
            height = (int)Math.Round(NativeHeight * scale);
        }

        return new PixelRect(client.X + ((client.Width - width) / 2), client.Y + ((client.Height - height) / 2), width, height);
    }
}
