using System.Runtime.InteropServices;
using Lockpick.Platform.Windows;
using Microsoft.Win32.SafeHandles;

namespace Lockpick.Autoplay;

/// <summary>
/// DEV RUNS ONLY. Applies a recorded state change in one go, for skipping cutscenes: each byte gets bits set and bits
/// cleared (<c>(value | set) &amp; ~clear</c>; a whole-byte write is set = new value, clear = its complement). Only the
/// saved game-state block (0xBE9620–0xBE9E1F: stage/room, every flag bank, inventory, item box — what a save holds) and
/// the message bytes (0xBE9825 choice/active, 0xBF0A16 message state) can be changed. Everything is written while the
/// game is suspended and read back.
/// </summary>
internal sealed partial class StateBlock : IDisposable
{
    private const ulong BlockStart = 0xBE9620;
    private const ulong BlockEnd = 0xBE9E20;
    private static readonly ulong[] MessageBytes = [0xBE9825, 0xBF0A16];
    private const ulong EventTable = 0xBF084C;
    private const int EventSlots = 8;
    private const int EventSize = 0x34;
    private const ulong StateFlags = 0xBE41C0;
    private const uint SceneFlags = 0x100000 | 0x20000 | 0x10000;

    private readonly SafeProcessHandle _process;
    private readonly int _processId;

    public StateBlock(int processId)
    {
        _processId = processId;
        // PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_QUERY_LIMITED_INFORMATION
        _process = OpenProcess(0x0008 | 0x0010 | 0x0020 | 0x1000, false, processId);
        if (_process.IsInvalid)
        {
            throw new InvalidOperationException($"cannot open the game (error {Marshal.GetLastPInvokeError()})");
        }
    }

    /// <summary>Applies every (address, set, clear); returns how many bytes changed.</summary>
    public int Apply(IReadOnlyList<(ulong Address, byte Set, byte Clear)> changes)
    {
        foreach (var (address, _, _) in changes)
        {
            if ((address < BlockStart || address >= BlockEnd) && !MessageBytes.Contains(address))
            {
                throw new ArgumentOutOfRangeException(nameof(changes), $"0x{address:X} is outside the saved state block");
            }
        }

        if (ProcessSuspender.Suspend(_processId) is { } error)
        {
            throw new InvalidOperationException("suspend failed: " + error);
        }

        try
        {
            var changed = 0;
            foreach (var (address, set, clear) in changes)
            {
                var buffer = new byte[1];
                if (!ReadProcessMemory(_process, (nint)address, buffer, 1, out _))
                {
                    throw new InvalidOperationException($"read 0x{address:X} failed");
                }

                var value = (byte)((buffer[0] | set) & ~clear);
                if (value == buffer[0])
                {
                    continue;
                }

                if (!WriteProcessMemory(_process, (nint)address, [value], 1, out var written) || written != 1
                    || !ReadProcessMemory(_process, (nint)address, buffer, 1, out _) || buffer[0] != value)
                {
                    throw new InvalidOperationException($"writing 0x{address:X} failed or did not read back");
                }

                changed++;
            }

            return changed;
        }
        finally
        {
            ProcessSuspender.Resume(_processId);
        }
    }

    /// <summary>
    /// Stops the scene that is playing: the 8 script event slots (0xBF084C, 0x34 bytes: state +0, active +2) go idle,
    /// the message closes (0xBE9825 bit 0x80, state 0xBF0A16) — the game loop takes no room change while a message is
    /// up (0x4810F7) — and the cutscene bits of the state flags clear (camera lock 0x100000, voice 0x20000, intensity
    /// ramp 0x10000). Followed by a room warp, the room reloads with whatever flags were applied.
    /// </summary>
    public void StopScene()
    {
        if (ProcessSuspender.Suspend(_processId) is { } error)
        {
            throw new InvalidOperationException("suspend failed: " + error);
        }

        try
        {
            for (var slot = 0; slot < EventSlots; slot++)
            {
                var entry = EventTable + (ulong)(slot * EventSize);
                WriteChecked(entry, [0]);
                WriteChecked(entry + 2, [0]);
            }

            WriteChecked(0xBE9825, [(byte)(ReadByte(0xBE9825) & 0x7F)]);
            WriteChecked(0xBF0A16, [0]);
            var flags = BitConverter.ToUInt32([ReadByte(StateFlags), ReadByte(StateFlags + 1), ReadByte(StateFlags + 2), ReadByte(StateFlags + 3)]);
            WriteChecked(StateFlags, BitConverter.GetBytes(flags & ~SceneFlags));
        }
        finally
        {
            ProcessSuspender.Resume(_processId);
        }
    }

    /// <summary>
    /// For enemies spawned outside their own rooms: the room's effect sprites (g_effectSpriteInfo, 50 pointers at
    /// 0xBF0A54, filled per room) only cover the effects of the room's own enemies, and Effect_CreateBillboard
    /// (0x47BE30) reads the header without a null check — a Hunter hit or a spider's attack in the dog corridor crashed
    /// the game at 0x47BFB6, and its animation table g_effectAnimData (0xBF0B1C) at 0x47C010 (2026-10-14). Each empty type (0xFFFFFFFF, or 0)
    /// gets a loaded type's sprite header and animation data: the effect may look wrong,
    /// nothing crashes. Undone by the next room load. Returns how many entries were filled.
    /// </summary>
    public int FillEffectSprites()
    {
        const ulong table = 0xBF0A54;     // g_effectSpriteInfo: sprite header per effect type
        const ulong anims = 0xBF0B1C;     // g_effectAnimData: animation data per effect type (read the same way)
        const int count = 50;
        if (ProcessSuspender.Suspend(_processId) is { } error)
        {
            throw new InvalidOperationException("suspend failed: " + error);
        }

        try
        {
            var raw = new byte[count * 4];
            var rawAnims = new byte[count * 4];
            if (!ReadProcessMemory(_process, (nint)table, raw, (nuint)raw.Length, out _)
                || !ReadProcessMemory(_process, (nint)anims, rawAnims, (nuint)rawAnims.Length, out _))
            {
                throw new InvalidOperationException("reading the effect sprite tables failed");
            }

            static bool Empty(uint e) => e is 0 or 0xFFFFFFFF;
            var entries = Enumerable.Range(0, count).Select(i => BitConverter.ToUInt32(raw, i * 4)).ToArray();
            var animEntries = Enumerable.Range(0, count).Select(i => BitConverter.ToUInt32(rawAnims, i * 4)).ToArray();
            // One loaded type whose sprite header and animation data both exist: each empty type gets both of its.
            var donor = Enumerable.Range(0, count).FirstOrDefault(i => !Empty(entries[i]) && !Empty(animEntries[i]), -1);
            if (donor < 0)
            {
                return 0;
            }

            var filled = 0;
            for (var i = 0; i < count; i++)
            {
                if (Empty(entries[i]) || Empty(animEntries[i]))
                {
                    WriteChecked(table + (ulong)(i * 4), BitConverter.GetBytes(entries[donor]));
                    WriteChecked(anims + (ulong)(i * 4), BitConverter.GetBytes(animEntries[donor]));
                    filled++;
                }
            }

            return filled;
        }
        finally
        {
            ProcessSuspender.Resume(_processId);
        }
    }

    public void Dispose() => _process.Dispose();

    private byte ReadByte(ulong address)
    {
        var buffer = new byte[1];
        if (!ReadProcessMemory(_process, (nint)address, buffer, 1, out _))
        {
            throw new InvalidOperationException($"read 0x{address:X} failed");
        }

        return buffer[0];
    }

    private void WriteChecked(ulong address, byte[] bytes)
    {
        var back = new byte[bytes.Length];
        if (!WriteProcessMemory(_process, (nint)address, bytes, (nuint)bytes.Length, out var written) || written != (nuint)bytes.Length
            || !ReadProcessMemory(_process, (nint)address, back, (nuint)back.Length, out _) || !back.AsSpan().SequenceEqual(bytes))
        {
            throw new InvalidOperationException($"writing 0x{address:X} failed or did not read back");
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
