using Lockpick.Core.Display;

namespace Lockpick.Platform.Windows;

/// <summary>
/// Finds the game's window and reads what an overlay needs from it, and keeps the overlay right above it
/// (docs/DISPLAY.md). Rectangles are physical pixels: Lockpick runs per-monitor DPI aware.
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

        // Several classes (one per build) are separated by ';'.
        foreach (var name in windowClass?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [null!])
        {
            var window = nint.Zero;
            while ((window = NativeMethods.FindWindowEx(0, window, name, null)) != 0)
            {
                if (OwnedBy(window, processId) && NativeMethods.IsWindowVisible(window))
                {
                    return window;
                }
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
    /// Keeps an overlay directly above the game in the window stack — not above everything: a window brought over the
    /// game covers the overlay too (owner report 2026-10-15: a topmost overlay floated over other apps). Topmost only
    /// while the game is (exclusive fullscreen). The game's own windows right above it (its IME window is owned by it
    /// and always sits there) are skipped: asking to go just below one of those puts the overlay below the whole game
    /// (live 2026-10-15: that is why the overlay vanished under exclusive fullscreen). Does nothing when the overlay
    /// already sits above the game with only the game's own windows in between. <paramref name="siblings"/> are the
    /// other overlay windows over the same game: they count as part of the stack, so two overlays don't keep swapping.
    /// </summary>
    public static bool KeepAbove(nint overlay, nint game, IReadOnlyCollection<nint>? siblings = null)
    {
        NativeMethods.GetWindowThreadProcessId(game, out var gamePid);
        var w = NativeMethods.GetWindow(game, NativeMethods.GwHwndPrev);
        while (w != 0)
        {
            if (w == overlay)
            {
                return true;
            }

            if (siblings?.Contains(w) != true
                && NativeMethods.GetWindow(w, NativeMethods.GwOwner) != game
                && !(NativeMethods.GetWindowThreadProcessId(w, out var pid) != 0 && pid == gamePid))
            {
                break; // the first other window above the game's own
            }

            w = NativeMethods.GetWindow(w, NativeMethods.GwHwndPrev);
        }

        const uint flags = NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate | NativeMethods.SwpNoOwnerZOrder;
        var gameTopmost = ((uint)NativeMethods.GetWindowLong(game, NativeMethods.GwlExStyle) & NativeMethods.WsExTopmost) != 0;
        var overlayTopmost = ((uint)NativeMethods.GetWindowLong(overlay, NativeMethods.GwlExStyle) & NativeMethods.WsExTopmost) != 0;
        if (gameTopmost != overlayTopmost)
        {
            NativeMethods.SetWindowPos(overlay, gameTopmost ? NativeMethods.HwndTopmost : NativeMethods.HwndNoTopmost, 0, 0, 0, 0, flags);
        }

        // Inserted just below that other window, i.e. right above the game and its own windows.
        return NativeMethods.SetWindowPos(overlay, w != 0 ? w : gameTopmost ? NativeMethods.HwndTopmost : NativeMethods.HwndTop, 0, 0, 0, 0, flags);
    }

    /// <summary>
    /// Puts <paramref name="window"/> directly above <paramref name="below"/> in the window stack (e.g. the time panel
    /// over the full-screen map), without activating it. Does nothing when it is already somewhere above it.
    /// </summary>
    public static void PlaceAbove(nint window, nint below)
    {
        for (var w = NativeMethods.GetWindow(below, NativeMethods.GwHwndPrev); w != 0; w = NativeMethods.GetWindow(w, NativeMethods.GwHwndPrev))
        {
            if (w == window)
            {
                return;
            }
        }

        // SetWindowPos puts a window below the one named: name the window just above `below`.
        var above = NativeMethods.GetWindow(below, NativeMethods.GwHwndPrev);
        NativeMethods.SetWindowPos(window, above != 0 ? above : NativeMethods.HwndTop, 0, 0, 0, 0,
            NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate | NativeMethods.SwpNoOwnerZOrder);
    }

    /// <summary>A window's outer rectangle (physical pixels).</summary>
    public static PixelRect Bounds(nint window) =>
        NativeMethods.GetWindowRect(window, out var r) ? new PixelRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top) : default;

    private static bool OwnedBy(nint window, int processId) =>
        NativeMethods.GetWindowThreadProcessId(window, out var owner) != 0 && owner == processId;
}
