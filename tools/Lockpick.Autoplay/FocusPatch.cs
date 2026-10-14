using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Lockpick.Autoplay;

/// <summary>
/// DEV RUNS ONLY. Keeps the game running when it isn't the foreground window (MEMORY_MAP "Focus and input"):
/// <list type="bullet">
/// <item><c>WM_ACTIVATE</c> no longer clears the focus flag (<c>0x44126B</c> → jump to <c>DefWindowProcA</c>), and the
/// flag at <c>0x4BCB2C</c> is set to 1 in case the game was already paused;</item>
/// <item>the keyboard map at <c>0xAC4030</c> is blanked, because the game polls <c>GetAsyncKeyState</c> and would
/// otherwise act on keys typed in other windows — the virtual controller still works. The game rewrites the map when
/// it loads its settings or a save (save offset 0x800), so a timer re-blanks it and remembers the game's latest map,
/// which is what gets restored.</item>
/// </list>
/// Every write checks the original bytes first and is read back; <see cref="Dispose"/> restores everything. Changes
/// live in the game's memory only (never its files) and end with the process.
/// </summary>
internal sealed partial class FocusPatch : IDisposable
{
    private const ulong ActivateClear = 0x44126B;
    private const ulong FocusFlag = 0x4BCB2C;
    private const ulong KeyMap = 0xAC4030;
    private const int KeyMapLength = 32;

    private static readonly byte[] ActivateOriginal = [0xC7, 0x05, 0x2C, 0xCB, 0x4B, 0x00, 0x00, 0x00, 0x00, 0x00];
    private static readonly byte[] ActivatePatched = [0xE9, 0xC6, 0x00, 0x00, 0x00, 0x90, 0x90, 0x90, 0x90, 0x90];

    private readonly SafeProcessHandle _process;
    private readonly Lock _gate = new();
    private readonly Action<string> _log;
    private readonly System.Threading.Timer _keepBlank;
    private byte[] _keyMap;
    private bool _restored;

    private FocusPatch(SafeProcessHandle process, byte[] keyMap, Action<string> log)
    {
        _process = process;
        _keyMap = keyMap;
        _log = log;
        _keepBlank = new System.Threading.Timer(_ => KeepBlank(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public static FocusPatch Apply(int processId, Action<string> log)
    {
        // PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_QUERY_LIMITED_INFORMATION
        var process = OpenProcess(0x0008 | 0x0010 | 0x0020 | 0x1000, false, processId);
        if (process.IsInvalid)
        {
            throw new InvalidOperationException($"cannot open the game for writing (error {Marshal.GetLastPInvokeError()})");
        }

        var current = Read(process, ActivateClear, ActivateOriginal.Length);
        if (!current.AsSpan().SequenceEqual(ActivateOriginal) && !current.AsSpan().SequenceEqual(ActivatePatched))
        {
            process.Dispose();
            throw new InvalidOperationException($"unexpected bytes at 0x{ActivateClear:X}: {Convert.ToHexString(current)} — not patching");
        }

        var keyMap = Read(process, KeyMap, KeyMapLength);
        var patch = new FocusPatch(process, keyMap, log);
        patch.WriteCode(ActivateClear, ActivatePatched);
        patch.WriteData(FocusFlag, BitConverter.GetBytes(1u));
        patch.WriteData(KeyMap, new byte[KeyMapLength]);
        patch._keepBlank.Change(250, 250);
        log($"focus patch on: the game keeps running in the background; keyboard map blanked (was {Convert.ToHexString(keyMap)})");
        return patch;
    }

    public void Dispose()
    {
        _keepBlank.Dispose();
        lock (_gate)
        {
            if (_restored)
            {
                return;
            }

            _restored = true;
        }

        try
        {
            WriteCode(ActivateClear, ActivateOriginal);
            WriteData(KeyMap, _keyMap);
        }
        catch (InvalidOperationException) when (HasExited())
        {
            // The game is gone; nothing to restore.
        }
        finally
        {
            _process.Dispose();
        }
    }

    private void KeepBlank()
    {
        lock (_gate)
        {
            if (_restored)
            {
                return;
            }

            try
            {
                var map = Read(_process, KeyMap, KeyMapLength);
                if (map.Any(b => b != 0))
                {
                    _keyMap = map;
                    WriteData(KeyMap, new byte[KeyMapLength]);
                    _log($"the game reloaded its keyboard map ({Convert.ToHexString(map)}) — blanked again");
                }
            }
            catch (InvalidOperationException)
            {
                // The game exited; Dispose handles the rest.
            }
        }
    }

    private bool HasExited() => GetExitCodeProcess(_process, out var code) && code != 259;

    private void WriteCode(ulong address, byte[] bytes)
    {
        if (!VirtualProtectEx(_process, (nint)address, (nuint)bytes.Length, 0x40, out var old))
        {
            throw new InvalidOperationException($"VirtualProtectEx 0x{address:X} failed ({Marshal.GetLastPInvokeError()})");
        }

        try
        {
            WriteData(address, bytes);
        }
        finally
        {
            VirtualProtectEx(_process, (nint)address, (nuint)bytes.Length, old, out _);
            FlushInstructionCache(_process, (nint)address, (nuint)bytes.Length);
        }
    }

    private void WriteData(ulong address, byte[] bytes)
    {
        if (!WriteProcessMemory(_process, (nint)address, bytes, (nuint)bytes.Length, out var written) || written != (nuint)bytes.Length)
        {
            throw new InvalidOperationException($"write 0x{address:X} failed ({Marshal.GetLastPInvokeError()})");
        }

        if (!Read(_process, address, bytes.Length).AsSpan().SequenceEqual(bytes))
        {
            throw new InvalidOperationException($"read-back 0x{address:X} differs");
        }
    }

    private static byte[] Read(SafeProcessHandle process, ulong address, int length)
    {
        var buffer = new byte[length];
        if (!ReadProcessMemory(process, (nint)address, buffer, (nuint)length, out var read) || read != (nuint)length)
        {
            throw new InvalidOperationException($"read 0x{address:X} failed ({Marshal.GetLastPInvokeError()})");
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

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool VirtualProtectEx(SafeProcessHandle process, nint address, nuint size, uint protect, out uint old);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlushInstructionCache(SafeProcessHandle process, nint address, nuint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
}
