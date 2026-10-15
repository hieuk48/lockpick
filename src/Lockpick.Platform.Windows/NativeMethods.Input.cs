using System.Runtime.InteropServices;

namespace Lockpick.Platform.Windows;

/// <summary>Controller input: XInput and HID Raw Input (button presses only; never the keyboard).</summary>
internal static partial class NativeMethods
{
    public const uint WmInput = 0x00FF;
    public const uint RidInput = 0x10000003;
    public const uint RimTypeHid = 2;
    public const uint RidevInputSink = 0x00000100;
    public const uint RidiDeviceName = 0x20000007;
    public const uint RidiPreparsedData = 0x20000005;
    public const uint RidiDeviceInfo = 0x2000000B;
    public const ushort HidUsagePageGeneric = 0x01;
    public const ushort HidUsageJoystick = 0x04;
    public const ushort HidUsageGamepad = 0x05;
    public const ushort HidUsagePageButton = 0x09;
    public const int HidpInput = 0;
    public const int HidpStatusSuccess = 0x00110000;
    public const uint PmRemove = 0x0001;
    public const uint QsAllInput = 0x04FF;
    public const nint HwndMessage = -3;

    [StructLayout(LayoutKind.Sequential)]
    public struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public nint Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RawInputHeader
    {
        public uint Type;
        public uint Size;
        public nint Device;
        public nint WParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RidDeviceInfoHid
    {
        public uint Size;
        public uint Type;
        public uint VendorId;
        public uint ProductId;
        public uint VersionNumber;
        public ushort UsagePage;
        public ushort Usage;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XInputState
    {
        public uint PacketNumber;
        public XInputGamepad Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct WindowClassEx
    {
        public uint Size;
        public uint Style;
        public delegate* unmanaged<nint, uint, nint, nint, nint> WndProc;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public char* MenuName;
        public char* ClassName;
        public nint IconSmall;
    }

    [LibraryImport("xinput1_4.dll")]
    public static partial uint XInputGetState(uint userIndex, out XInputState state);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool RegisterRawInputDevices(RawInputDevice* devices, uint count, uint size);

    [LibraryImport("user32.dll")]
    public static unsafe partial uint GetRawInputData(nint rawInput, uint command, void* data, ref uint size, uint headerSize);

    [LibraryImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW")]
    public static unsafe partial uint GetRawInputDeviceInfo(nint device, uint command, void* data, ref uint size);

    [LibraryImport("hid.dll")]
    public static unsafe partial int HidP_GetUsages(int reportType, ushort usagePage, ushort linkCollection, ushort* usageList,
        ref uint usageLength, void* preparsedData, byte* report, uint reportLength);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
    public static unsafe partial ushort RegisterClassEx(WindowClassEx* windowClass);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateWindowEx(uint exStyle, string className, string? windowName, uint style, int x, int y,
        int width, int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyWindow(nint window);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    public static partial nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PeekMessage(out Msg message, nint window, uint filterMin, uint filterMax, uint remove);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(in Msg message);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    public static partial nint DispatchMessage(in Msg message);

    [LibraryImport("user32.dll")]
    public static unsafe partial uint MsgWaitForMultipleObjects(uint count, nint* handles, [MarshalAs(UnmanagedType.Bool)] bool waitAll,
        uint milliseconds, uint wakeMask);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint GetModuleHandle(string? moduleName);
}
