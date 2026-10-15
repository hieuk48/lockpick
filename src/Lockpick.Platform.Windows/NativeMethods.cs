using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Lockpick.Platform.Windows;

internal static partial class NativeMethods
{
    public const uint ProcessQueryLimitedInformation = 0x1000;
    public const uint ProcessVmRead = 0x0010;
    public const uint ProcessVmWrite = 0x0020;
    public const uint ProcessVmOperation = 0x0008;
    public const uint Synchronize = 0x00100000;

    public const uint WaitObject0 = 0;
    public const uint ListModulesAll = 0x03;
    public const uint CreateWaitableTimerHighResolution = 0x00000002;
    public const uint TimerAllAccess = 0x1F0003;

    [StructLayout(LayoutKind.Sequential)]
    public struct ModuleInfo
    {
        public nint BaseOfDll;
        public uint SizeOfImage;
        public nint EntryPoint;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool ReadProcessMemory(SafeProcessHandle process, nint baseAddress, byte* buffer, nuint size, out nuint bytesRead);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool WriteProcessMemory(SafeProcessHandle process, nint baseAddress, byte* buffer, nuint size, out nuint written);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, char* exeName, ref uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWow64Process(SafeProcessHandle process, [MarshalAs(UnmanagedType.Bool)] out bool wow64Process);

    [LibraryImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool EnumProcessModulesEx(SafeProcessHandle process, nint* modules, uint cb, out uint needed, uint filterFlag);

    [LibraryImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetModuleInformation(SafeProcessHandle process, nint module, out ModuleInfo info, uint cb);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeWaitHandle CreateWaitableTimerEx(nint attributes, string? name, uint flags, uint desiredAccess);

    public const uint WmHotkey = 0x0312;
    public const uint WmQuit = 0x0012;
    public const uint ModNoRepeat = 0x4000;

    [StructLayout(LayoutKind.Sequential)]
    public struct Msg
    {
        public nint Hwnd;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int PointX;
        public int PointY;
        public uint Private;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint virtualKey);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(nint hwnd, int id);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)]
    public static partial int GetMessage(out Msg message, nint hwnd, uint filterMin, uint filterMax);

    [LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostThreadMessage(uint threadId, uint message, nuint wParam, nint lParam);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWaitableTimer(SafeWaitHandle timer, in long dueTime, int period, nint completionRoutine, nint argToCompletionRoutine, [MarshalAs(UnmanagedType.Bool)] bool resume);
}
