using System.Runtime.InteropServices;
using Lockpick.Platform.Windows;
using Microsoft.Win32.SafeHandles;

namespace Lockpick.Autoplay;

/// <summary>
/// DEV RUNS ONLY. Empties inventory slots so automated pickup tests never hit "no room": the 8 u16 slots at
/// 0xBE9944 (profile fields inv0–inv7: item id, quantity in the high byte). Only slots from the given index up are
/// cleared (the starting weapons stay), never the equipped one, and the rest are compacted to the front: the status
/// screen stops at the first empty slot (2026-10-14: a gap hid the music notes). The game is suspended for the write,
/// which is read back.
/// </summary>
internal sealed partial class InventorySlots : IDisposable
{
    private const ulong Slots = 0xBE9944;
    private const ulong EquippedSlot = 0xBE9849;
    private const int Count = 8;

    private readonly SafeProcessHandle _process;
    private readonly int _processId;

    public InventorySlots(int processId)
    {
        _processId = processId;
        // PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_QUERY_LIMITED_INFORMATION
        _process = OpenProcess(0x0008 | 0x0010 | 0x0020 | 0x1000, false, processId);
        if (_process.IsInvalid)
        {
            throw new InvalidOperationException($"cannot open the game (error {Marshal.GetLastPInvokeError()})");
        }
    }

    /// <summary>Clears slots <paramref name="from"/>..7 except the equipped one; returns how many were cleared.</summary>
    public int ClearFrom(int from)
    {
        if (from is < 1 or > Count)
        {
            throw new ArgumentOutOfRangeException(nameof(from), "1–8 (slot 0 always stays)");
        }

        if (ProcessSuspender.Suspend(_processId) is { } error)
        {
            throw new InvalidOperationException("suspend failed: " + error);
        }

        try
        {
            var equipped = Read(EquippedSlot, 1)[0];
            var slots = Read(Slots, Count * 2);
            var kept = new List<ushort>();
            var cleared = 0;
            for (var slot = 0; slot < Count; slot++)
            {
                var value = BitConverter.ToUInt16(slots, slot * 2);
                if (slot < from || slot == equipped)
                {
                    if (value != 0)
                    {
                        kept.Add(value);
                    }
                }
                else if (value != 0)
                {
                    cleared++;
                }
            }

            if (equipped < Count && from <= equipped)
            {
                // Compacting would move the equipped item; leave the layout alone in that case.
                return 0;
            }

            var compacted = new byte[Count * 2];
            for (var i = 0; i < kept.Count; i++)
            {
                BitConverter.GetBytes(kept[i]).CopyTo(compacted, i * 2);
            }

            Write(Slots, compacted);
            return cleared;
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
