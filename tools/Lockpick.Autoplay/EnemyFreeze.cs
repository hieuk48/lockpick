using System.Runtime.InteropServices;
using Lockpick.Platform.Windows;
using Microsoft.Win32.SafeHandles;

namespace Lockpick.Autoplay;

/// <summary>
/// DEV RUNS ONLY. Stops the current room's enemies from acting during sweeps (pickups, placement tests): sets the
/// active enemy count (u8 0xBE41E2, profile field enemyCount) to 0. update_entities (0x48F0F0, disassembled
/// 2026-10-14) starts with <c>cmp byte ptr [0xBE41E2], 0</c> and returns when it is 0, so no enemy logic runs; the
/// next room load sets the count again. (Clearing the per-enemy "active" bits instead would leave the count non-zero
/// and the update loop would walk past the enemy list.) The game is suspended for the write, which is read back.
/// </summary>
internal sealed partial class EnemyFreeze : IDisposable
{
    private const ulong EnemyCount = 0xBE41E2;

    private readonly SafeProcessHandle _process;
    private readonly int _processId;

    public EnemyFreeze(int processId)
    {
        _processId = processId;
        // PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_QUERY_LIMITED_INFORMATION
        _process = OpenProcess(0x0008 | 0x0010 | 0x0020 | 0x1000, false, processId);
        if (_process.IsInvalid)
        {
            throw new InvalidOperationException($"cannot open the game (error {Marshal.GetLastPInvokeError()})");
        }
    }

    /// <summary>Returns the enemy count before freezing.</summary>
    public byte Freeze()
    {
        if (ProcessSuspender.Suspend(_processId) is { } error)
        {
            throw new InvalidOperationException("suspend failed: " + error);
        }

        try
        {
            var buffer = new byte[1];
            if (!ReadProcessMemory(_process, (nint)EnemyCount, buffer, 1, out _))
            {
                throw new InvalidOperationException("read 0xBE41E2 failed");
            }

            var before = buffer[0];
            if (before > 30)
            {
                throw new InvalidOperationException($"implausible enemy count {before}");
            }

            byte[] zero = [0];
            if (!WriteProcessMemory(_process, (nint)EnemyCount, zero, 1, out var written) || written != 1
                || !ReadProcessMemory(_process, (nint)EnemyCount, buffer, 1, out _) || buffer[0] != 0)
            {
                throw new InvalidOperationException("writing the enemy count failed or did not read back");
            }

            return before;
        }
        finally
        {
            ProcessSuspender.Resume(_processId);
        }
    }

    /// <summary>
    /// Undoes <see cref="Freeze"/> in the same room: writes the count back (1–30) while it still reads 0, so the room's
    /// enemies act again — combat tests freeze them while the player raises a gun at close range, then thaw and fire.
    /// </summary>
    public void Thaw(byte count)
    {
        if (count is < 1 or > 30)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "1–30");
        }

        if (ProcessSuspender.Suspend(_processId) is { } error)
        {
            throw new InvalidOperationException("suspend failed: " + error);
        }

        try
        {
            var buffer = new byte[1];
            if (!ReadProcessMemory(_process, (nint)EnemyCount, buffer, 1, out _) || buffer[0] != 0)
            {
                throw new InvalidOperationException($"the enemy count isn't frozen (reads {buffer[0]})");
            }

            byte[] value = [count];
            if (!WriteProcessMemory(_process, (nint)EnemyCount, value, 1, out var written) || written != 1
                || !ReadProcessMemory(_process, (nint)EnemyCount, buffer, 1, out _) || buffer[0] != count)
            {
                throw new InvalidOperationException("writing the enemy count failed or did not read back");
            }
        }
        finally
        {
            ProcessSuspender.Resume(_processId);
        }
    }

    public void Dispose() => _process.Dispose();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReadProcessMemory(SafeProcessHandle process, nint address, [Out] byte[] buffer, nuint size, out nuint read);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WriteProcessMemory(SafeProcessHandle process, nint address, byte[] buffer, nuint size, out nuint written);
}
