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
    /// ("chris" or "jill") and keeps the room variant right until the player is placed in the first room; returns the
    /// time to the title and to the first room, and how often the variant had to be put back.
    /// </summary>
    public (TimeSpan Title, TimeSpan Room, int Restored) Start(string character, TimeSpan timeout)
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
                var restored = HoldVariant(id == 1, clock, timeout);
                return (title, clock.Elapsed, restored);
            }

            Thread.Sleep(5);
        }

        throw new TimeoutException($"the title screen never came (task 0 at 0x{BitConverter.ToUInt32(Read(TasksEip, 4)):X})");
    }

    private int HoldVariant(bool jill, Stopwatch clock, TimeSpan timeout)
    {
        var restored = 0;
        while (clock.Elapsed < timeout)
        {
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

            if (BitConverter.ToInt32(Read(PlayerX, 4)) != 0 || BitConverter.ToInt32(Read(PlayerZ, 4)) != 0)
            {
                return restored;
            }

            Thread.Sleep(2);
        }

        throw new TimeoutException("the first room never loaded");
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
