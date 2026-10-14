using System.Runtime.InteropServices;
using Lockpick.Platform.Windows;
using Microsoft.Win32.SafeHandles;

namespace Lockpick.Autoplay;

/// <summary>
/// DEV RUNS ONLY. Sets one active enemy's HP so a damage test can keep the same enemy alive through every weapon (a
/// test "trap": with the room's enemies frozen between shots it never moves, and its HP after each hit gives the
/// damage exactly). Enemy slots at 0xBE6464, 30 × 0x18C bytes: +0x00 bit 0 active, +0x88 HP (i16) — verified live by
/// the combat tests 2026-10-14. Only an active slot is written; the game is suspended for the write, which is read back.
/// </summary>
internal sealed partial class EnemyHealth : IDisposable
{
    private const ulong Slots = 0xBE6464;
    private const int SlotSize = 0x18C;
    private const int SlotCount = 30;

    private readonly SafeProcessHandle _process;
    private readonly int _processId;

    public EnemyHealth(int processId)
    {
        _processId = processId;
        // PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_QUERY_LIMITED_INFORMATION
        _process = OpenProcess(0x0008 | 0x0010 | 0x0020 | 0x1000, false, processId);
        if (_process.IsInvalid)
        {
            throw new InvalidOperationException($"cannot open the game (error {Marshal.GetLastPInvokeError()})");
        }
    }

    /// <summary>Sets slot <paramref name="slot"/>'s HP to <paramref name="hp"/> (1–5000); returns the HP it had.</summary>
    public short Set(int slot, int hp)
    {
        if (slot is < 0 or >= SlotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(slot), "0–29");
        }

        if (hp is < 1 or > 5000)
        {
            throw new ArgumentOutOfRangeException(nameof(hp), "1–5000");
        }

        var baseAddress = Slots + (ulong)(slot * SlotSize);
        if (ProcessSuspender.Suspend(_processId) is { } error)
        {
            throw new InvalidOperationException("suspend failed: " + error);
        }

        try
        {
            var flags = new byte[1];
            if (!ReadProcessMemory(_process, (nint)baseAddress, flags, 1, out _) || (flags[0] & 1) == 0)
            {
                throw new InvalidOperationException($"enemy slot {slot} is not active");
            }

            var buffer = new byte[2];
            if (!ReadProcessMemory(_process, (nint)(baseAddress + 0x88), buffer, 2, out _))
            {
                throw new InvalidOperationException("reading the enemy's HP failed");
            }

            var before = BitConverter.ToInt16(buffer);
            var value = BitConverter.GetBytes((short)hp);
            if (!WriteProcessMemory(_process, (nint)(baseAddress + 0x88), value, 2, out var written) || written != 2
                || !ReadProcessMemory(_process, (nint)(baseAddress + 0x88), buffer, 2, out _) || BitConverter.ToInt16(buffer) != hp)
            {
                throw new InvalidOperationException("writing the enemy's HP failed or did not read back");
            }

            return before;
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
