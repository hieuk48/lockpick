using System.Runtime.InteropServices;
using Lockpick.Platform.Windows;
using Microsoft.Win32.SafeHandles;

namespace Lockpick.Autoplay;

/// <summary>
/// DEV RUNS ONLY. Sets one active enemy's HP, or its position, so a damage test can keep the same enemy alive and in
/// one place through every weapon (a test "trap", owner's idea 2026-10-14: frozen between shots, placed at a fixed spot
/// in front of the player at the weapon's reach, its HP after each hit gives the damage exactly). Enemy slots at
/// 0xBE6464, 30 × 0x18C bytes: +0x00 bit 0 active, +0x34/+0x3C X/Z (i32), +0x88 HP (i16) — verified live by the combat
/// tests 2026-10-14. Only an active slot is written; the game is suspended for the write, which is read back.
/// </summary>
internal sealed partial class EnemyHealth : IDisposable
{
    private const ulong Slots = 0xBE6464;
    private const int SlotSize = 0x18C;
    private const int SlotCount = 30;

    private readonly SafeProcessHandle _process;
    private readonly int _processId;
    private readonly Dictionary<int, byte[]> _snapshots = [];

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

    /// <summary>
    /// Sets slot <paramref name="slot"/>'s type byte (+0x01) to <paramref name="type"/> (0–0x13, enemy types only) and
    /// returns the old one. With the AI paused (EnemyAi) one enemy can stand in for every type in a damage test: the
    /// damage comes from the weapon table row for the type (decomp apply_weapon_damage).
    /// </summary>
    public byte SetType(int slot, int type)
    {
        if (type is < 0 or > 0x13)
        {
            throw new ArgumentOutOfRangeException(nameof(type), "enemy types 0–0x13");
        }

        var old = ReadByte(slot, 0x01);
        WriteByte(slot, 0x01, (byte)type);
        return old;
    }

    /// <summary>
    /// Sets the aim-height bits (0xE0 of +0x00) of slot <paramref name="slot"/>: a shot only takes an enemy whose bits
    /// match the player's aim, and an enemy sets them only once it notices the player (idle 0x41, aware 0xF1 — live
    /// 2026-10-14), which in a damage test means letting it run up to the player. The active bit stays as it is.
    /// </summary>
    public void SetTargetable(int slot) => WriteByte(slot, 0x00, (byte)(ReadByte(slot, 0x00) | 0xE0));

    /// <summary>
    /// Keeps a copy of slot <paramref name="slot"/>'s whole entry (0x18C bytes) so <see cref="Restore"/> can put it back:
    /// a stand-in's few frames under another type's AI can scramble it (2026-10-14: the Hunter's moved a zombie to
    /// x ≈ 2·10⁹), and restoring after every shot undoes whatever that AI did.
    /// </summary>
    public void Snapshot(int slot)
    {
        if ((ReadByte(slot, 0) & 1) == 0)
        {
            throw new InvalidOperationException($"enemy slot {slot} is not active");
        }

        var buffer = new byte[SlotSize];
        if (!ReadProcessMemory(_process, (nint)(Slots + (ulong)(slot * SlotSize)), buffer, SlotSize, out var read) || read != SlotSize)
        {
            throw new InvalidOperationException("reading the enemy slot failed");
        }

        _snapshots[slot] = buffer;
    }

    /// <summary>Writes back the copy <see cref="Snapshot"/> took of slot <paramref name="slot"/> (read back).</summary>
    public void Restore(int slot)
    {
        if (!_snapshots.TryGetValue(slot, out var saved))
        {
            throw new InvalidOperationException($"no snapshot of enemy slot {slot}");
        }

        if (ProcessSuspender.Suspend(_processId) is { } error)
        {
            throw new InvalidOperationException("suspend failed: " + error);
        }

        try
        {
            var address = (nint)(Slots + (ulong)(slot * SlotSize));
            var back = new byte[SlotSize];
            if (!WriteProcessMemory(_process, address, saved, SlotSize, out var written) || written != SlotSize
                || !ReadProcessMemory(_process, address, back, SlotSize, out _) || !back.AsSpan().SequenceEqual(saved))
            {
                throw new InvalidOperationException("restoring the enemy slot failed or did not read back");
            }
        }
        finally
        {
            ProcessSuspender.Resume(_processId);
        }
    }

    /// <summary>Clears slot <paramref name="slot"/>'s hit state (+0x8A): a shot only takes an enemy whose hit state is
    /// 0, and with the AI paused nothing clears it after a hit.</summary>
    public void ClearHit(int slot) => WriteByte(slot, 0x8A, 0);

    /// <summary>
    /// Sets the AI state (+0x84), the index into the type's state table; returns the old one. For zombies state 4 is
    /// zombie_no_action (a bare RET, decomp zombie_states_table 0x4BB2C8): the zombie stays drawn and hittable but its
    /// AI does nothing, so it stands still. State 1 hands it back to its behaviour.
    /// </summary>
    public byte SetState(int slot, byte state)
    {
        var was = ReadByte(slot, 0x84);
        WriteByte(slot, 0x84, state);
        return was;
    }

    /// <summary>
    /// Waits for slot <paramref name="slot"/> to be set up afresh (a room load's enemy_set: active, AI state 0) and for
    /// its own init to run (state no longer 0), then puts it in <paramref name="state"/> — on its first frames, before
    /// it can move. Returns how long that took.
    /// </summary>
    public TimeSpan HoldWhenReady(int slot, byte state, TimeSpan timeout)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var fresh = false;
        while (clock.Elapsed < timeout)
        {
            var active = (ReadByte(slot, 0x00) & 1) != 0;
            var now = ReadByte(slot, 0x84);
            fresh |= active && now == 0;
            if (fresh && active && now != 0)
            {
                WriteByte(slot, 0x84, state);
                return clock.Elapsed;
            }

            Thread.Yield();
        }

        throw new TimeoutException($"enemy slot {slot} was not set up again within {timeout.TotalSeconds:F0} s");
    }

    private byte ReadByte(int slot, int offset)
    {
        if (slot is < 0 or >= SlotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(slot), "0–29");
        }

        var buffer = new byte[1];
        if (!ReadProcessMemory(_process, (nint)(Slots + (ulong)(slot * SlotSize + offset)), buffer, 1, out _))
        {
            throw new InvalidOperationException("reading the enemy slot failed");
        }

        return buffer[0];
    }

    private void WriteByte(int slot, int offset, byte value)
    {
        if ((ReadByte(slot, 0) & 1) == 0)
        {
            throw new InvalidOperationException($"enemy slot {slot} is not active");
        }

        if (ProcessSuspender.Suspend(_processId) is { } error)
        {
            throw new InvalidOperationException("suspend failed: " + error);
        }

        try
        {
            var address = (nint)(Slots + (ulong)(slot * SlotSize + offset));
            if (!WriteProcessMemory(_process, address, [value], 1, out var written) || written != 1 || ReadByte(slot, offset) != value)
            {
                throw new InvalidOperationException("writing the enemy slot failed or did not read back");
            }
        }
        finally
        {
            ProcessSuspender.Resume(_processId);
        }
    }

    /// <summary>Moves slot <paramref name="slot"/> to (<paramref name="x"/>, <paramref name="z"/>) (room units, ±32767).</summary>
    public void Place(int slot, int x, int z)
    {
        if (slot is < 0 or >= SlotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(slot), "0–29");
        }

        if (x is < -32767 or > 32767 || z is < -32767 or > 32767)
        {
            throw new ArgumentOutOfRangeException(nameof(x), "room coordinates are within ±32767");
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

            foreach (var (offset, value) in new[] { (0x34UL, x), (0x3CUL, z) })
            {
                var bytes = BitConverter.GetBytes(value);
                var back = new byte[4];
                if (!WriteProcessMemory(_process, (nint)(baseAddress + offset), bytes, 4, out var written) || written != 4
                    || !ReadProcessMemory(_process, (nint)(baseAddress + offset), back, 4, out _) || BitConverter.ToInt32(back) != value)
                {
                    throw new InvalidOperationException("writing the enemy's position failed or did not read back");
                }
            }
        }
        finally
        {
            ProcessSuspender.Resume(_processId);
        }
    }

    /// <summary>
    /// Starts slot <paramref name="slot"/>'s enemy afresh at (<paramref name="x"/>, <paramref name="z"/>) facing
    /// <paramref name="angle"/>, the way the room script's enemy_set does (decomp cmd_enemy_set 0x4617D0): position (the
    /// matrix ints +0x34/+0x3C and the shorts +0x6C/+0x70), yaw +0x74, behaviour flags +0x02, then AI state +0x84,
    /// ignore-player +0x85, action +0x86/+0x87 and hit state +0x8A back to 0 so its own init runs on the next frame.
    /// The slot must hold an enemy whose model the room loaded (a dormant scene enemy, or one to move).
    /// </summary>
    public void Wake(int slot, int x, int z, short angle, byte behavior)
    {
        if (slot is < 0 or >= SlotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(slot), "0–29");
        }

        if (x is < -32767 or > 32767 || z is < -32767 or > 32767)
        {
            throw new ArgumentOutOfRangeException(nameof(x), "room coordinates are within ±32767");
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
                throw new InvalidOperationException($"enemy slot {slot} is not active (the room loaded no enemy there)");
            }

            (ulong Offset, byte[] Bytes)[] writes =
            [
                (0x34, BitConverter.GetBytes(x)), (0x3C, BitConverter.GetBytes(z)),
                (0x6C, BitConverter.GetBytes((short)x)), (0x70, BitConverter.GetBytes((short)z)),
                (0x74, BitConverter.GetBytes(angle)), (0x02, [behavior]),
                (0x84, [0, 0, 0, 0]), (0x8A, [0]),
            ];
            foreach (var (offset, bytes) in writes)
            {
                var back = new byte[bytes.Length];
                if (!WriteProcessMemory(_process, (nint)(baseAddress + offset), bytes, (nuint)bytes.Length, out var written) || written != (nuint)bytes.Length
                    || !ReadProcessMemory(_process, (nint)(baseAddress + offset), back, (nuint)back.Length, out _) || !back.AsSpan().SequenceEqual(bytes))
                {
                    throw new InvalidOperationException($"writing enemy slot {slot} +0x{offset:X} failed or did not read back");
                }
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
