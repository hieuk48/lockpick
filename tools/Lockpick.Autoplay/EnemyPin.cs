using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Lockpick.Autoplay;

/// <summary>
/// DEV RUNS ONLY. Holds enemies still that have no do-nothing state of their own (crows, wasps and snakes have none,
/// decomp state tables): a thread rewrites each pinned slot's position (matrix ints +0x34..+0x3F, shorts +0x6C..+0x71),
/// facing (+0x74) and AI state (+0x84..+0x87) about every millisecond from a copy taken on its first frame, so what its
/// AI does is undone before it shows. Health (+0x88) and hit state (+0x8A) are left alone: shots still land and
/// count. Skipping the enemy's update instead would also stop it being drawn. Writes are not synchronised with the
/// game (a frame may show a sliver of movement); pins end on unpin or when the harness lets go of the game.
/// </summary>
internal sealed partial class EnemyPin : IDisposable
{
    private const ulong Slots = 0xBE6464;
    private const int SlotSize = 0x18C;
    private static readonly (int Offset, int Length)[] Pinned = [(0x34, 12), (0x6C, 6), (0x74, 2), (0x84, 4)];
    // Position only (x/y/z ints and shorts): the enemy's AI, reactions, falls and death run as usual, it just can't
    // leave the spot — for watching what hits do to it.
    private static readonly (int Offset, int Length)[] PinnedPosition = [(0x34, 12), (0x6C, 6)];

    private readonly SafeProcessHandle _process;
    private readonly Dictionary<int, (byte[][] Copy, bool PositionOnly)> _pins = [];
    private readonly Dictionary<int, (byte? State, bool Fresh, bool PositionOnly)> _armed = [];
    private readonly Lock _lock = new();
    private readonly Thread _thread;
    private volatile bool _stop;

    public EnemyPin(int processId)
    {
        // PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_QUERY_LIMITED_INFORMATION
        _process = OpenProcess(0x0008 | 0x0010 | 0x0020 | 0x1000, false, processId);
        if (_process.IsInvalid)
        {
            throw new InvalidOperationException($"cannot open the game (error {Marshal.GetLastPInvokeError()})");
        }

        _thread = new Thread(Run) { IsBackground = true, Name = "enemy pin", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>Pins <paramref name="slot"/> where and as it is now (or, with <paramref name="positionOnly"/>, just where).</summary>
    public void Pin(int slot, bool positionOnly = false)
    {
        if (slot is < 0 or >= 30)
        {
            throw new ArgumentOutOfRangeException(nameof(slot), "0–29");
        }

        var copy = (positionOnly ? PinnedPosition : Pinned).Select(p => Read(Address(slot, p.Offset), p.Length)).ToArray();
        lock (_lock)
        {
            _pins[slot] = (copy, positionOnly);
        }
    }

    /// <summary>
    /// Waits for <paramref name="slot"/> to be set up afresh (active, AI state 0) and for its own init to run (state no
    /// longer 0), then pins it — on its first frames, before it can move. Returns how long that took.
    /// </summary>
    public TimeSpan PinWhenReady(int slot, TimeSpan timeout)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var fresh = false;
        while (clock.Elapsed < timeout)
        {
            var head = Read(Address(slot, 0), 1)[0];
            var state = Read(Address(slot, 0x84), 1)[0];
            fresh |= (head & 1) != 0 && state == 0;
            if (fresh && (head & 1) != 0 && state != 0)
            {
                Pin(slot);
                return clock.Elapsed;
            }

            Thread.Yield();
        }

        throw new TimeoutException($"enemy slot {slot} was not set up again within {timeout.TotalSeconds:F0} s");
    }

    /// <summary>
    /// Arms <paramref name="slot"/>: the next time it is set up afresh (active, AI state 0) and its init has run (state
    /// no longer 0), it is held — pinned (<paramref name="state"/> null) or put in a do-nothing state — by this thread,
    /// so several slots set up by one room load are each caught on their own first frames. Returns at once.
    /// </summary>
    public void Arm(int slot, byte? state, bool positionOnly = false)
    {
        if (slot is < 0 or >= 30)
        {
            throw new ArgumentOutOfRangeException(nameof(slot), "0–29");
        }

        lock (_lock)
        {
            _pins.Remove(slot);
            _armed[slot] = (state, false, positionOnly);
        }
    }

    /// <summary>Slots armed but not yet caught.</summary>
    public int[] Waiting()
    {
        lock (_lock)
        {
            return [.. _armed.Keys];
        }
    }

    public bool Unpin(int slot)
    {
        lock (_lock)
        {
            return _pins.Remove(slot) | _armed.Remove(slot);
        }
    }

    private void CatchArmed()
    {
        KeyValuePair<int, (byte? State, bool Fresh, bool PositionOnly)>[] armed;
        lock (_lock)
        {
            armed = [.. _armed];
        }

        foreach (var (slot, (state, wasFresh, positionOnly)) in armed)
        {
            var head = Read(Address(slot, 0), 1)[0];
            var now = Read(Address(slot, 0x84), 1)[0];
            var fresh = wasFresh || ((head & 1) != 0 && now == 0);
            if (fresh && (head & 1) != 0 && now != 0)
            {
                if (state is { } s)
                {
                    WriteProcessMemory(_process, (nint)Address(slot, 0x84), [s], 1, out _);
                }

                var copy = state is null
                    ? (positionOnly ? PinnedPosition : Pinned).Select(p => Read(Address(slot, p.Offset), p.Length)).ToArray()
                    : null;
                lock (_lock)
                {
                    _armed.Remove(slot);
                    if (copy is not null)
                    {
                        _pins[slot] = (copy, positionOnly);
                    }
                }
            }
            else if (fresh != wasFresh)
            {
                lock (_lock)
                {
                    if (_armed.ContainsKey(slot))
                    {
                        _armed[slot] = (state, fresh, positionOnly);
                    }
                }
            }
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(500);
        _process.Dispose();
    }

    private static ulong Address(int slot, int offset) => Slots + (ulong)(slot * SlotSize + offset);

    private void Run()
    {
        while (!_stop)
        {
            try
            {
                CatchArmed();
            }
            catch (InvalidOperationException)
            {
                // The game went away or is between rooms; try again on the next pass.
            }

            KeyValuePair<int, (byte[][] Copy, bool PositionOnly)>[] pins;
            lock (_lock)
            {
                pins = [.. _pins];
            }

            foreach (var (slot, (copy, positionOnly)) in pins)
            {
                var fields = positionOnly ? PinnedPosition : Pinned;
                for (var i = 0; i < fields.Length; i++)
                {
                    WriteProcessMemory(_process, (nint)Address(slot, fields[i].Offset), copy[i], (nuint)copy[i].Length, out _);
                }
            }

            if (pins.Length == 0 && _armed.Count == 0)
            {
                Thread.Sleep(20);
            }
            else
            {
                Thread.Sleep(0);
            }
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

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReadProcessMemory(SafeProcessHandle process, nint address, [Out] byte[] buffer, nuint size, out nuint read);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WriteProcessMemory(SafeProcessHandle process, nint address, byte[] buffer, nuint size, out nuint written);
}
