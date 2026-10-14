using System.Runtime.InteropServices;
using Lockpick.Platform.Windows;
using Microsoft.Win32.SafeHandles;

namespace Lockpick.Autoplay;

/// <summary>
/// DEV RUNS ONLY. Cuts to a camera of the current room the way the game defers its own cuts: the camera id
/// (g_roomCameraId, u8 0xBE9822 — profile field camera) and state-flag bit 0x20 (camera redraw), which game_loop
/// consumes next frame by calling display_room_camera_bg (0x462D50; decomp check_camera_switch / game_loop). Used
/// after placing the player, because the game only tests the current camera's switch zones and never re-cuts on its
/// own after a teleport. The caller must pick a camera that has a group in the room's switch-zone table
/// (tools/rdt/rdt_map.py camera_for): the redraw searches that table and runs off its end otherwise.
/// </summary>
internal sealed partial class CameraCut : IDisposable
{
    private const ulong CameraId = 0xBE9822;
    private const ulong StateFlags = 0xBE41C0;
    private const uint CameraRedraw = 0x20;
    private const uint CameraLock = 0x100000;

    private readonly SafeProcessHandle _process;
    private readonly int _processId;

    public CameraCut(int processId)
    {
        _processId = processId;
        // PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_QUERY_LIMITED_INFORMATION
        _process = OpenProcess(0x0008 | 0x0010 | 0x0020 | 0x1000, false, processId);
        if (_process.IsInvalid)
        {
            throw new InvalidOperationException($"cannot open the game (error {Marshal.GetLastPInvokeError()})");
        }
    }

    public void Cut(byte camera)
    {
        if (camera >= 32)
        {
            throw new ArgumentOutOfRangeException(nameof(camera), "a room has at most 32 cameras");
        }

        if (ProcessSuspender.Suspend(_processId) is { } error)
        {
            throw new InvalidOperationException("suspend failed: " + error);
        }

        try
        {
            var flags = BitConverter.ToUInt32(Read(StateFlags, 4));
            if ((flags & CameraLock) != 0)
            {
                throw new InvalidOperationException("camera changes are locked (cutscene)");
            }

            Write(CameraId, [camera]);
            Write(StateFlags, BitConverter.GetBytes(flags | CameraRedraw));
        }
        finally
        {
            ProcessSuspender.Resume(_processId);
        }
    }

    public void Dispose() => _process.Dispose();

    private byte[] Read(ulong address, int length)
    {
        var buffer = new byte[length];
        if (!ReadProcessMemory(_process, (nint)address, buffer, (nuint)length, out var read) || read != (nuint)length)
        {
            throw new InvalidOperationException($"read 0x{address:X} failed");
        }

        return buffer;
    }

    private void Write(ulong address, byte[] bytes)
    {
        var back = new byte[bytes.Length];
        if (!WriteProcessMemory(_process, (nint)address, bytes, (nuint)bytes.Length, out var written) || written != (nuint)bytes.Length
            || !ReadProcessMemory(_process, (nint)address, back, (nuint)back.Length, out _) || !back.AsSpan().SequenceEqual(bytes))
        {
            throw new InvalidOperationException($"write 0x{address:X} failed or did not read back");
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReadProcessMemory(SafeProcessHandle process, nint address, [Out] byte[] buffer, nuint size, out nuint read);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WriteProcessMemory(SafeProcessHandle process, nint address, byte[] buffer, nuint size, out nuint written);
}
