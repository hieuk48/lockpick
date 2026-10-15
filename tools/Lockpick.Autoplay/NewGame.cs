using System.Diagnostics;
using System.Runtime.InteropServices;
using Lockpick.Platform.Windows;
using Microsoft.Win32.SafeHandles;

namespace Lockpick.Autoplay;

/// <summary>
/// DEV RUNS ONLY. Starts a new game straight from a fresh boot, with no menu presses. The game runs its screens as
/// tasks (decomp TaskScheduler): a screen hands over by writing the next screen's entry to <c>g_TasksEIP[id]</c>
/// (0xD91A80) and START (2) to the task's state (<c>g_TasksTable</c> 0xD1FDE4, 0x7C each), and the scheduler enters it
/// fresh on the next pass. When the title screen ends with no input the game itself chains from it straight to
/// <c>game_start</c> (the attract demo, decomp title_state case 0), so this does the same once the title has run to
/// its first sleep (0x43056C) — past its sounds_reset (0x47EB70, which sets the room-music table the first room
/// needs; handing over before it crashed the game in update_room_bgm on 2026-10-14) and its sound bank load: character
/// byte 0xBE984B (0 Chris, 1 Jill; the character card writes it), the Jill room-variant bit
/// 0x800000 of the state flags, then game_start (0x480710), whose InitializeGame builds the new game (HP, starting
/// items, clock at zero). The write happens only while the task isn't running (current task id ≠ 0), with the game
/// suspended; otherwise the task's next yield would overwrite the state. The room-variant bit is then held until the
/// player stands in the first room: the logo movie restores the flags it saved when it ends, which wiped the bit on
/// the first try (2026-10-14: Jill ran Chris's room 106 and the game crashed in the opening scene). Code bytes are
/// checked first, so another build fails instead of being patched.
/// </summary>
/// <summary>Where a cutscene leaves the game: saved-state bits to set/clear, and the player's room and spot.</summary>
internal sealed record SceneEnd(byte Room, byte Camera, short X, short Z, short Yaw, IReadOnlyList<(ulong Address, byte Set, byte Clear)> Bits);

internal sealed partial class NewGame : IDisposable
{
    private const ulong TasksEip = 0xD91A80;
    private const ulong TasksTable = 0xD1FDE4;
    private const ulong CurrentTaskId = 0xD91A7C;
    private const ulong SelectedCharacter = 0xBE984B;
    private const ulong StateFlags = 0xBE41C0;
    private const uint JillVariant = 0x800000;
    private const uint GameStart = 0x480710;
    private const uint TitleState = 0x430470;
    private const ulong TitleMenu = 0xD22777; // 1 while the title is up; its exit (0x430E10) clears it
    private const ulong PlayerX = 0xBE6318;
    private const ulong PlayerZ = 0xBE6320;
    private const ulong PlayerYaw = 0xBE6358;
    private const ulong SpawnX = 0xBE6350;
    private const ulong SpawnZ = 0xBE6354;
    private const ulong RoomByte = 0xBE9821;
    private const ulong CameraByte = 0xBE9822;
    private const ulong TasksEsp = 0xD91A70;
    private const ulong StateBlockStart = 0xBE9620;
    private const int StateBlockSize = 0x800;
    // InitializeGame sleeps one frame here (call at 0x480A44) after building the new game and before loading the first
    // room (0x480AB6): the sleeping task's saved stack holds this return address 40 bytes up (pushal, pushfd, call).
    private const uint BeforeFirstRoom = 0x480A49;

    // game_start: mov dword [g_playingGameFlag 0x4D4674], 1 — title_state: sub esp,8; and dword [0xBE41C0], …
    private static readonly (uint Address, byte[] Bytes)[] Signatures =
    [
        (GameStart, [0xC7, 0x05, 0x74, 0x46, 0x4D, 0x00, 0x01, 0x00, 0x00, 0x00]),
        (TitleState, [0x83, 0xEC, 0x08, 0x81, 0x25, 0xC0, 0x41, 0xBE, 0x00]),
    ];

    private readonly SafeProcessHandle _process;
    private readonly int _processId;

    public NewGame(int processId)
    {
        _processId = processId;
        // PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_QUERY_LIMITED_INFORMATION
        _process = OpenProcess(0x0008 | 0x0010 | 0x0020 | 0x1000, false, processId);
        if (_process.IsInvalid)
        {
            throw new InvalidOperationException($"cannot open the game (error {Marshal.GetLastPInvokeError()})");
        }
    }

    /// <summary>
    /// Waits for the main task to reach the title screen, hands it to game_start as <paramref name="character"/>
    /// ("chris" or "jill"), keeps the room variant right, and stops the game at the frame before the first room loads.
    /// There, <paramref name="opening"/> (the opening's recorded end) is applied so the game loads straight into the room
    /// where the opening hands over control, and the opening never plays. Returns the time to the title and to that
    /// frame, how often the variant had to be put back, and the saved-state block as it was at that frame.
    /// </summary>
    public (TimeSpan Title, TimeSpan Room, int Restored, byte[] Block) Start(string character, TimeSpan timeout, SceneEnd? opening = null)
    {
        var id = character.ToLowerInvariant() switch
        {
            "chris" => (byte)0,
            "jill" => (byte)1,
            _ => throw new ArgumentException("chris or jill (Rebecca is played inside Chris's game)", nameof(character)),
        };
        foreach (var (address, bytes) in Signatures)
        {
            if (!Read(address, bytes.Length).AsSpan().SequenceEqual(bytes))
            {
                throw new InvalidOperationException($"unexpected code at 0x{address:X}: not the GOG 1.0 build this was checked on");
            }
        }

        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            if (BitConverter.ToUInt32(Read(TasksEip, 4)) == TitleState && TryHandOver(id))
            {
                var title = clock.Elapsed;
                var (restored, block) = HoldVariant(id == 1, clock, timeout, opening);
                return (title, clock.Elapsed, restored, block);
            }

            Thread.Sleep(5);
        }

        throw new TimeoutException($"the title screen never came (task 0 at 0x{BitConverter.ToUInt32(Read(TasksEip, 4)):X})");
    }

    private (int Restored, byte[] Block) HoldVariant(bool jill, Stopwatch clock, TimeSpan timeout, SceneEnd? opening)
    {
        var restored = 0;
        while (clock.Elapsed < timeout)
        {
            if (BeforeFirstRoomLoads() && TryFirstRoom(jill, opening) is { } block)
            {
                return (restored, block);
            }

            if (((BitConverter.ToUInt32(Read(StateFlags, 4)) & JillVariant) != 0) != jill)
            {
                if (ProcessSuspender.Suspend(_processId) is { } error)
                {
                    throw new InvalidOperationException("suspend failed: " + error);
                }

                try
                {
                    var flags = BitConverter.ToUInt32(Read(StateFlags, 4));
                    Write(StateFlags, BitConverter.GetBytes(jill ? flags | JillVariant : flags & ~JillVariant));
                    restored++;
                }
                finally
                {
                    ProcessSuspender.Resume(_processId);
                }
            }

            // Spin, don't sleep: the frame before the first room is one tick (~33 ms) and Thread.Sleep(1) can take ~15 ms
            // (2026-10-14: missed once). It comes a fraction of a second after the handover.
            Thread.Yield();
        }

        throw new TimeoutException("the frame before the first room never came");
    }

    private bool BeforeFirstRoomLoads() =>
        BitConverter.ToUInt32(Read(TasksEip, 4)) == GameStart && BitConverter.ToInt16(Read(TasksTable, 2)) == 1
        && BitConverter.ToUInt32(Read(BitConverter.ToUInt32(Read(TasksEsp, 4)) + 40UL, 4)) == BeforeFirstRoom;

    private byte[]? TryFirstRoom(bool jill, SceneEnd? opening)
    {
        if (ProcessSuspender.Suspend(_processId) is { } error)
        {
            throw new InvalidOperationException("suspend failed: " + error);
        }

        try
        {
            if (BitConverter.ToUInt32(Read(CurrentTaskId, 4)) == 0 || !BeforeFirstRoomLoads())
            {
                return null;
            }

            var flags = BitConverter.ToUInt32(Read(StateFlags, 4));
            Write(StateFlags, BitConverter.GetBytes(jill ? flags | JillVariant : flags & ~JillVariant));
            var block = Read(StateBlockStart, StateBlockSize);
            if (opening is not null)
            {
                foreach (var (address, set, clear) in opening.Bits)
                {
                    if (address < StateBlockStart || address >= StateBlockStart + StateBlockSize)
                    {
                        throw new ArgumentOutOfRangeException(nameof(opening), $"0x{address:X} is outside the saved state block");
                    }

                    Write(address, [(byte)((Read(address, 1)[0] | set) & ~clear)]);
                }

                Write(RoomByte, [opening.Room]);
                Write(CameraByte, [opening.Camera]);
                // The new game's spawn spot (InitPlayerData 0x481880 writes 17000, 5000, 0xC00 here); the room load
                // places the player from it.
                Write(SpawnX, BitConverter.GetBytes(opening.X));
                Write(SpawnZ, BitConverter.GetBytes(opening.Z));
                Write(PlayerYaw, BitConverter.GetBytes(opening.Yaw));
                Write(PlayerX, BitConverter.GetBytes((int)opening.X));
                Write(PlayerZ, BitConverter.GetBytes((int)opening.Z));
            }

            return block;
        }
        finally
        {
            ProcessSuspender.Resume(_processId);
        }
    }

    public void Dispose() => _process.Dispose();

    private bool TryHandOver(byte character)
    {
        if (ProcessSuspender.Suspend(_processId) is { } error)
        {
            throw new InvalidOperationException("suspend failed: " + error);
        }

        try
        {
            // Only between task runs (the scheduler's current id is 0 while the title task itself is executing), and
            // only once the title sleeps (state 1): its setup has run.
            if (BitConverter.ToUInt32(Read(CurrentTaskId, 4)) == 0 || BitConverter.ToUInt32(Read(TasksEip, 4)) != TitleState
                || BitConverter.ToInt16(Read(TasksTable, 2)) != 1)
            {
                return false;
            }

            var flags = BitConverter.ToUInt32(Read(StateFlags, 4));
            Write(SelectedCharacter, [character]);
            Write(StateFlags, BitConverter.GetBytes(character == 1 ? flags | JillVariant : flags & ~JillVariant));
            Write(TitleMenu, [0]); // the title's exit isn't run; the app reads this byte as "not in a game"
            Write(TasksEip, BitConverter.GetBytes(GameStart));
            Write(TasksTable, [2, 0, 0, 0]); // state START, sleep counter 0
            return true;
        }
        finally
        {
            ProcessSuspender.Resume(_processId);
        }
    }

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
