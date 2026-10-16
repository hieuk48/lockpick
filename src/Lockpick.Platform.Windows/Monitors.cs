using System.Runtime.InteropServices;
using Lockpick.Core.Display;

namespace Lockpick.Platform.Windows;

/// <summary>A monitor: where it is (physical pixels), its work area, and the resolutions its driver offers.</summary>
public sealed record MonitorDescription(string Device, PixelRect Bounds, PixelRect Work, bool Primary, IReadOnlyList<(int Width, int Height, int Hz)> Modes)
{
    /// <summary>"Main monitor", "Monitor 2", … in the order <see cref="Monitors.List"/> gives.</summary>
    public string Name { get; init; } = Primary ? "Main monitor" : "Monitor";

    public override string ToString() => $"{Name} — {Bounds.Width}×{Bounds.Height}";
}

/// <summary>The monitors and their resolutions (for the game's display settings and moving its window).</summary>
public static partial class Monitors
{
    private const int EnumCurrentSettings = -1;

    /// <summary>Every monitor attached to the desktop, the main one first.</summary>
    public static IReadOnlyList<MonitorDescription> List()
    {
        var found = new List<MonitorDescription>();
        for (uint i = 0; i < 16; i++)
        {
            var device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevices(null, i, ref device, 0))
            {
                break;
            }

            if ((device.StateFlags & 1) == 0)
            {
                continue; // not attached to the desktop
            }

            var current = new DevMode { Size = (short)Marshal.SizeOf<DevMode>() };
            if (!EnumDisplaySettings(device.DeviceName, EnumCurrentSettings, ref current))
            {
                continue;
            }

            var bounds = new PixelRect(current.PositionX, current.PositionY, current.PelsWidth, current.PelsHeight);
            var info = new NativeMethods.MonitorInfo { Size = (uint)Marshal.SizeOf<NativeMethods.MonitorInfo>() };
            var center = new NativeMethods.Point { X = bounds.X + (bounds.Width / 2), Y = bounds.Y + (bounds.Height / 2) };
            var work = NativeMethods.GetMonitorInfo(MonitorFromPoint(center, 2), ref info)
                ? new PixelRect(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top)
                : bounds;
            found.Add(new MonitorDescription(device.DeviceName, bounds, work, (device.StateFlags & 4) != 0, Modes(device.DeviceName)));
        }

        return [.. found.OrderByDescending(m => m.Primary).Select((m, i) => m with { Name = m.Primary ? "Main monitor" : $"Monitor {i + 1}" })];
    }

    /// <summary>
    /// Moves a window so its client area is centred in <paramref name="monitor"/>'s work area (top-aligned when it is
    /// taller), without activating it. The game's own window can't be dragged; this is how players move it.
    /// </summary>
    public static bool MoveWindowTo(nint window, MonitorDescription monitor)
    {
        var outer = GameWindows.Bounds(window);
        if (outer.IsEmpty)
        {
            return false;
        }

        var x = monitor.Work.X + Math.Max(0, (monitor.Work.Width - outer.Width) / 2);
        var y = monitor.Work.Y + Math.Max(0, (monitor.Work.Height - outer.Height) / 2);
        return NativeMethods.SetWindowPos(window, 0, x, y, 0, 0,
            NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate | NativeMethods.SwpNoZOrder | NativeMethods.SwpNoOwnerZOrder);
    }

    private static IReadOnlyList<(int Width, int Height, int Hz)> Modes(string device)
    {
        var modes = new Dictionary<(int, int), int>();
        var mode = new DevMode { Size = (short)Marshal.SizeOf<DevMode>() };
        for (var i = 0; EnumDisplaySettings(device, i, ref mode); i++)
        {
            if (mode.PelsWidth >= 640 && mode.PelsHeight >= 480 && mode.BitsPerPel >= 32)
            {
                var key = (mode.PelsWidth, mode.PelsHeight);
                modes[key] = Math.Max(modes.GetValueOrDefault(key), mode.DisplayFrequency);
            }
        }

        return [.. modes.Select(m => (m.Key.Item1, m.Key.Item2, m.Value)).OrderBy(m => m.Item1 * m.Item2).ThenBy(m => m.Item1)];
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        public short SpecVersion;
        public short DriverVersion;
        public short Size;
        public short DriverExtra;
        public int Fields;
        public int PositionX;
        public int PositionY;
        public int DisplayOrientation;
        public int DisplayFixedOutput;
        public short Color;
        public short Duplex;
        public short YResolution;
        public short TtOption;
        public short Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string FormName;
        public short LogPixels;
        public int BitsPerPel;
        public int PelsWidth;
        public int PelsHeight;
        public int DisplayFlags;
        public int DisplayFrequency;
        public int IcmMethod;
        public int IcmIntent;
        public int MediaType;
        public int DitherType;
        public int Reserved1;
        public int Reserved2;
        public int PanningWidth;
        public int PanningHeight;
    }

#pragma warning disable SYSLIB1054 // ByValTStr structs need the classic marshaller.
    [DllImport("user32.dll", EntryPoint = "EnumDisplayDevicesW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string? device, uint index, ref DisplayDevice displayDevice, uint flags);

    [DllImport("user32.dll", EntryPoint = "EnumDisplaySettingsW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(string device, int mode, ref DevMode devMode);
#pragma warning restore SYSLIB1054

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromPoint(NativeMethods.Point point, uint flags);
}
