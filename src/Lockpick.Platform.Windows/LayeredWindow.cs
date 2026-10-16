using System.Runtime.InteropServices;
using Lockpick.Core.Display;

namespace Lockpick.Platform.Windows;

/// <summary>
/// A plain Win32 layered window that shows a picture with per-pixel transparency (UpdateLayeredWindow): click-through,
/// never activated, not in Alt+Tab. Lockpick's overlay draws through this rather than a UI-toolkit window, because over
/// exclusive fullscreen the toolkit's window ended up below the game within 200 ms of every raise while a plain
/// layered window stayed above it (live 2026-10-15, docs/DISPLAY.md). Create and use it on one thread with a message
/// loop (the UI thread).
/// </summary>
public sealed partial class LayeredWindow : IDisposable
{
    private const string ClassName = "LockpickOverlay";
    private static readonly WndProc Procedure = (h, m, w, l) => DefWindowProc(h, m, w, l);
    private static bool _registered;

    public LayeredWindow(string title)
    {
        Register();
        const uint exStyle = NativeMethods.WsExLayered | NativeMethods.WsExTransparent | NativeMethods.WsExNoActivate | NativeMethods.WsExToolWindow;
        Handle = CreateWindowEx(exStyle, ClassName, title, WsPopup, 0, 0, 1, 1, 0, 0, GetModuleHandle(null), 0);
    }

    private delegate nint WndProc(nint window, uint message, nint wParam, nint lParam);

    public nint Handle { get; private set; }

    public bool IsVisible => Handle != 0 && NativeMethods.IsWindowVisible(Handle);

    /// <summary>
    /// Shows <paramref name="pixels"/> (32-bit premultiplied BGRA, <paramref name="width"/> × <paramref name="height"/>,
    /// rows top to bottom) at <paramref name="x"/>, <paramref name="y"/> (physical pixels), without activating.
    /// </summary>
    public unsafe bool Show(int x, int y, int width, int height, ReadOnlySpan<byte> pixels)
    {
        if (Handle == 0 || width <= 0 || height <= 0 || pixels.Length < width * height * 4)
        {
            return false;
        }

        var screen = GetDC(0);
        var memory = CreateCompatibleDC(screen);
        var header = new BitmapInfoHeader
        {
            Size = (uint)sizeof(BitmapInfoHeader), Width = width, Height = -height, Planes = 1, BitCount = 32,
        };
        var bitmap = CreateDIBSection(memory, ref header, 0, out var bits, 0, 0);
        try
        {
            if (bitmap == 0)
            {
                return false;
            }

            pixels[..(width * height * 4)].CopyTo(new Span<byte>((void*)bits, width * height * 4));
            var old = SelectObject(memory, bitmap);
            var position = new NativeMethods.Point { X = x, Y = y };
            var size = new Size { Cx = width, Cy = height };
            var source = new NativeMethods.Point();
            var blend = new BlendFunction { Op = 0, Flags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 }; // AC_SRC_ALPHA
            var ok = UpdateLayeredWindow(Handle, screen, ref position, ref size, memory, ref source, 0, ref blend, 2); // ULW_ALPHA
            SelectObject(memory, old);
            if (ok && !NativeMethods.IsWindowVisible(Handle))
            {
                ShowWindow(Handle, 8); // SW_SHOWNA
            }

            return ok;
        }
        finally
        {
            if (bitmap != 0)
            {
                DeleteObject(bitmap);
            }

            DeleteDC(memory);
            _ = ReleaseDC(0, screen);
        }
    }

    /// <summary>Moves the window without redrawing its picture.</summary>
    public bool Move(int x, int y) =>
        NativeMethods.SetWindowPos(Handle, 0, x, y, 0, 0, NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate | NativeMethods.SwpNoZOrder | NativeMethods.SwpNoOwnerZOrder);

    public void Hide()
    {
        if (IsVisible)
        {
            ShowWindow(Handle, 0);
        }
    }

    public PixelRect Bounds => GameWindows.Bounds(Handle);

    public void Dispose()
    {
        if (Handle != 0)
        {
            DestroyWindow(Handle);
            Handle = 0;
        }
    }

    private static unsafe void Register()
    {
        if (_registered)
        {
            return;
        }

        var cls = new WindowClass
        {
            Size = (uint)sizeof(WindowClass),
            Procedure = Marshal.GetFunctionPointerForDelegate(Procedure),
            Instance = GetModuleHandle(null),
            ClassName = Marshal.StringToHGlobalUni(ClassName),
        };
        _registered = RegisterClassEx(ref cls) != 0 || Marshal.GetLastWin32Error() == 1410; // already registered
    }

    private const uint WsPopup = 0x80000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowClass
    {
        public uint Size;
        public uint Style;
        public nint Procedure;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public nint MenuName;
        public nint ClassName;
        public nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Size
    {
        public int Cx;
        public int Cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlendFunction
    {
        public byte Op;
        public byte Flags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
    private static partial ushort RegisterClassEx(ref WindowClass windowClass);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static partial nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint window, int command);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateLayeredWindow(nint window, nint screenDc, ref NativeMethods.Point position, ref Size size, nint sourceDc, ref NativeMethods.Point sourcePosition, uint colorKey, ref BlendFunction blend, uint flags);

    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint window);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint window, nint dc);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleDC(nint dc);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(nint dc);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateDIBSection(nint dc, ref BitmapInfoHeader header, uint usage, out nint bits, nint section, uint offset);

    [LibraryImport("gdi32.dll")]
    private static partial nint SelectObject(nint dc, nint obj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint obj);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandle(string? name);
}
