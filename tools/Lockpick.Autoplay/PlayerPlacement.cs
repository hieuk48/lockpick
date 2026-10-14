using System.Runtime.InteropServices;
using Lockpick.Platform.Windows;
using Microsoft.Win32.SafeHandles;

namespace Lockpick.Autoplay;

/// <summary>
/// DEV RUNS ONLY. Stands the player at a position in the current room (e.g. an item's pickup zone from the room
/// file) for automated tests. Writes only the player's X and Z (i32, profile fields positionX 0xBE6318 / positionZ
/// 0xBE6320) and facing (i16, yaw 0xBE6358), with the game suspended, and reads them back.
/// </summary>
internal sealed partial class PlayerPlacement : IDisposable
{
    private const ulong PositionX = 0xBE6318;
    private const ulong PositionZ = 0xBE6320;
    private const ulong Yaw = 0xBE6358;

    private readonly SafeProcessHandle _process;
    private readonly int _processId;

    public PlayerPlacement(int processId)
    {
        _processId = processId;
        // PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_QUERY_LIMITED_INFORMATION
        _process = OpenProcess(0x0008 | 0x0010 | 0x0020 | 0x1000, false, processId);
        if (_process.IsInvalid)
        {
            throw new InvalidOperationException($"cannot open the game (error {Marshal.GetLastPInvokeError()})");
        }
    }

    public void Place(int x, int z, short? angle)
    {
        // Room coordinates are 16-bit in the room files; anything else is a mistake.
        if (x is < short.MinValue or > ushort.MaxValue || z is < short.MinValue or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(x), "room coordinates are 16-bit");
        }

        if (ProcessSuspender.Suspend(_processId) is { } error)
        {
            throw new InvalidOperationException("suspend failed: " + error);
        }

        try
        {
            Write(PositionX, BitConverter.GetBytes(x));
            Write(PositionZ, BitConverter.GetBytes(z));
            if (angle is { } a)
            {
                Write(Yaw, BitConverter.GetBytes(a));
            }
        }
        finally
        {
            ProcessSuspender.Resume(_processId);
        }
    }

    public void Dispose() => _process.Dispose();

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
