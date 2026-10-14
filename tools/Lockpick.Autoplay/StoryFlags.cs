using System.Runtime.InteropServices;
using Lockpick.Platform.Windows;
using Microsoft.Win32.SafeHandles;

namespace Lockpick.Autoplay;

/// <summary>
/// DEV RUNS ONLY. Sets or clears one story flag so a test can stand in a story state without replaying the story
/// (cutscene triggers, "go back" doors): bank A at 0xBE98C0 (128 flags) or bank B at 0xBE9854 (256 flags), flag n =
/// bit 31 − n % 32 of the little-endian word n / 32 (docs/MEMORY_MAP.md). Room scripts read the banks on the next
/// room load. One bit is changed; the game is suspended for the write, which is read back.
/// </summary>
internal sealed partial class StoryFlags : IDisposable
{
    private const ulong BankA = 0xBE98C0;
    private const ulong BankB = 0xBE9854;

    private readonly SafeProcessHandle _process;
    private readonly int _processId;

    public StoryFlags(int processId)
    {
        _processId = processId;
        // PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_QUERY_LIMITED_INFORMATION
        _process = OpenProcess(0x0008 | 0x0010 | 0x0020 | 0x1000, false, processId);
        if (_process.IsInvalid)
        {
            throw new InvalidOperationException($"cannot open the game (error {Marshal.GetLastPInvokeError()})");
        }
    }

    /// <summary>Sets (<paramref name="on"/>) or clears flag <paramref name="flag"/> of bank 'A' or 'B'; returns the previous state.</summary>
    public bool Set(char bank, int flag, bool on)
    {
        var (address, count) = char.ToUpperInvariant(bank) switch
        {
            'A' => (BankA, 128),
            'B' => (BankB, 256),
            _ => throw new ArgumentOutOfRangeException(nameof(bank), "bank A or B"),
        };
        if (flag < 0 || flag >= count)
        {
            throw new ArgumentOutOfRangeException(nameof(flag), $"0..{count - 1}");
        }

        var word = address + (ulong)(flag / 32 * 4);
        var mask = 0x80000000u >> (flag % 32);
        if (ProcessSuspender.Suspend(_processId) is { } error)
        {
            throw new InvalidOperationException("suspend failed: " + error);
        }

        try
        {
            var buffer = new byte[4];
            if (!ReadProcessMemory(_process, (nint)word, buffer, 4, out _))
            {
                throw new InvalidOperationException($"read 0x{word:X} failed");
            }

            var before = BitConverter.ToUInt32(buffer);
            var after = on ? before | mask : before & ~mask;
            var bytes = BitConverter.GetBytes(after);
            if (!WriteProcessMemory(_process, (nint)word, bytes, 4, out var written) || written != 4
                || !ReadProcessMemory(_process, (nint)word, buffer, 4, out _) || BitConverter.ToUInt32(buffer) != after)
            {
                throw new InvalidOperationException($"writing 0x{word:X} failed or did not read back");
            }

            return (before & mask) != 0;
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
