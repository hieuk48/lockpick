using Lockpick.Core.Display;

namespace Lockpick.Platform.Windows;

/// <summary>
/// Finds the game's window and reads what an overlay needs from it, and makes Lockpick's overlay window click-through
/// and non-activating (docs/DISPLAY.md). Rectangles are physical pixels: Lockpick runs per-monitor DPI aware.
/// </summary>
public static class GameWindows
{
    /// <summary>The game process's visible top-level window of the given class (any class when null), or null.</summary>
    public static nint Find(int processId, string? windowClass, nint previous = 0)
    {
        if (previous != 0 && NativeMethods.IsWindow(previous) && OwnedBy(previous, processId))
        {
            return previous;
        }

        var window = nint.Zero;
        while ((window = NativeMethods.FindWindowEx(0, window, windowClass, null)) != 0)
        {
            if (OwnedBy(window, processId) && NativeMethods.IsWindowVisible(window))
            {
                return window;
            }
        }

        return 0;
    }

    /// <summary>The window's state, or null when it is gone.</summary>
    public static GameWindowInfo? Read(nint window)
    {
        if (window == 0 || !NativeMethods.IsWindow(window) || !NativeMethods.GetClientRect(window, out var client))
        {
            return null;
        }

        var origin = new NativeMethods.Point();
        NativeMethods.ClientToScreen(window, ref origin);
        var clientRect = new PixelRect(origin.X, origin.Y, client.Right - client.Left, client.Bottom - client.Top);
        var info = new NativeMethods.MonitorInfo { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        NativeMethods.GetMonitorInfo(NativeMethods.MonitorFromWindow(window, NativeMethods.MonitorDefaultToNearest), ref info);
        var monitor = new PixelRect(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top);
        var style = (uint)NativeMethods.GetWindowLong(window, NativeMethods.GwlStyle);
        var exStyle = (uint)NativeMethods.GetWindowLong(window, NativeMethods.GwlExStyle);
        var minimized = NativeMethods.IsIconic(window);
        return new GameWindowInfo(
            window,
            clientRect,
            monitor,
            (int)NativeMethods.GetDpiForWindow(window),
            minimized,
            NativeMethods.IsWindowVisible(window),
            NativeMethods.GetForegroundWindow() == window,
            GameWindowInfo.Classify((style & NativeMethods.WsCaption) == NativeMethods.WsCaption,
                (exStyle & NativeMethods.WsExTopmost) != 0, clientRect, monitor));
    }

    /// <summary>
    /// Makes an overlay window click-through, non-activating and absent from Alt+Tab: layered + transparent (mouse
    /// input goes to the window below), no-activate (the game keeps focus — it pauses otherwise), tool window.
    /// </summary>
    public static bool MakeOverlay(nint window)
    {
        var exStyle = (uint)NativeMethods.GetWindowLong(window, NativeMethods.GwlExStyle);
        exStyle |= NativeMethods.WsExLayered | NativeMethods.WsExTransparent | NativeMethods.WsExNoActivate
            | NativeMethods.WsExToolWindow | NativeMethods.WsExTopmost;
        _ = NativeMethods.SetWindowLong(window, NativeMethods.GwlExStyle, unchecked((int)exStyle));
        return NativeMethods.SetLayeredWindowAttributes(window, 0, 255, NativeMethods.LwaAlpha)
            && ((uint)NativeMethods.GetWindowLong(window, NativeMethods.GwlExStyle) & NativeMethods.WsExTransparent) != 0;
    }

    /// <summary>Moves and sizes an overlay window (physical pixels), topmost, without activating it.</summary>
    public static bool Place(nint window, PixelRect rect) =>
        NativeMethods.SetWindowPos(window, NativeMethods.HwndTopmost, rect.X, rect.Y, rect.Width, rect.Height,
            NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow | NativeMethods.SwpNoOwnerZOrder);

    /// <summary>A window's outer rectangle (physical pixels).</summary>
    public static PixelRect Bounds(nint window) =>
        NativeMethods.GetWindowRect(window, out var r) ? new PixelRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top) : default;

    private static bool OwnedBy(nint window, int processId) =>
        NativeMethods.GetWindowThreadProcessId(window, out var owner) != 0 && owner == processId;
}
