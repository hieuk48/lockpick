using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Lockpick.Autoplay;

/// <summary>
/// DEV RUNS ONLY. Fast-forwards the game by shortening its tick interval, so cutscenes and scripted waits finish
/// sooner with every side effect intact (nothing is skipped). The main loop's limiter (0x441F0C–0x441F75, this
/// build) runs <c>main_loop</c> once per <c>[0x4BCB54]</c> ms when <c>[0x4D46D8]</c> != 0 and <c>[0x4BCB48]</c> == 1.
/// Only the interval is written (bounded, read back); the original is restored on dispose.
/// </summary>
internal sealed partial class GameSpeed : IDisposable
{
    private const ulong LimiterOn = 0x4D46D8;
    private const ulong UseInterval = 0x4BCB48;
    private const ulong Interval = 0x4BCB54;

    private readonly SafeProcessHandle _process;
    private readonly uint _original;

    public GameSpeed(int processId)
    {
        // PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_QUERY_LIMITED_INFORMATION
        _process = OpenProcess(0x0008 | 0x0010 | 0x0020 | 0x1000, false, processId);
        if (_process.IsInvalid)
        {
            throw new InvalidOperationException($"cannot open the game (error {Marshal.GetLastPInvokeError()})");
        }

        _original = ReadU32(Interval);
    }

    public uint OriginalMs => _original;

    public string Describe() =>
        $"limiter {(ReadU32(LimiterOn) != 0 ? "on" : "off")}, interval mode {ReadU32(UseInterval)}, interval {ReadU32(Interval)} ms (normal {_original} ms)";

    /// <summary>Sets the tick interval in ms (1–100; the game's own is 33 ms = 30 ticks/s).</summary>
    public void SetIntervalMs(uint ms)
    {
        if (ms is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(ms), "1–100 ms");
        }

        var bytes = BitConverter.GetBytes(ms);
        if (!WriteProcessMemory(_process, (nint)Interval, bytes, 4, out var written) || written != 4 || ReadU32(Interval) != ms)
        {
            throw new InvalidOperationException("writing the tick interval failed or did not read back");
        }
    }

    public void Dispose()
    {
        try
        {
            SetIntervalMs(_original);
        }
        catch (InvalidOperationException)
        {
            // The game has exited.
        }
        finally
        {
            _process.Dispose();
        }
    }

    private uint ReadU32(ulong address)
    {
        var buffer = new byte[4];
        if (!ReadProcessMemory(_process, (nint)address, buffer, 4, out var read) || read != 4)
        {
            throw new InvalidOperationException($"read 0x{address:X} failed");
        }

        return BitConverter.ToUInt32(buffer);
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
