using System.Runtime.InteropServices;
using Lockpick.Platform.Windows;
using Microsoft.Win32.SafeHandles;

namespace Lockpick.Autoplay;

/// <summary>
/// DEV RUNS ONLY. Runs an extra room-init script right after the room's own, for spawning or swapping enemies: room
/// init (disassembled 2026-10-14) runs the room's init script at 0x477B29 —
/// <code>
///   mov eax, [0xD213BC]   push eax   call 0x473F60 (run_command_functions)   add esp, 4
/// </code>
/// — and after it loads one model per active enemy slot, so an enemy_set run here gets its model, animations and AI
/// loaded like one that belongs to the room. The 14 bytes are replaced by a jump to a small block that runs the room's
/// script, then ours, then jumps back to 0x477B37. The original bytes are checked before patching and put back by
/// <see cref="Remove"/> (meant for one room load); the change is in the game's memory only.
/// </summary>
internal sealed partial class RoomScriptHook : IDisposable
{
    private const uint Site = 0x477B29;
    private const uint ReturnTo = 0x477B37;
    private const uint RunCommandFunctions = 0x473F60;
    private const uint InitScriptPointer = 0xD213BC;
    private static readonly byte[] Original = [0xA1, 0xBC, 0x13, 0xD2, 0x00, 0x50, 0xE8, 0x2C, 0xC4, 0xFF, 0xFF, 0x83, 0xC4, 0x04];

    private readonly SafeProcessHandle _process;
    private readonly int _processId;
    private nint _buffer;
    private bool _applied;

    public RoomScriptHook(int processId)
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
    /// Installs <paramref name="script"/> (run_command_functions format: blocks of [u16 size incl. itself][statements
    /// ending in 00 00], then a 0 size) to run after the room's own init script on every room load until removed.
    /// </summary>
    public void Apply(byte[] script)
    {
        if (script.Length is < 4 or > 0x100 || BitConverter.ToUInt16(script, script.Length - 2) != 0)
        {
            throw new ArgumentException("a script is 4–256 bytes and ends with a 0 block size", nameof(script));
        }

        if (_buffer == 0)
        {
            // MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE: the jump block at +0, the script at +0x100.
            _buffer = VirtualAllocEx(_process, 0, 0x200, 0x3000, 0x40);
            if (_buffer == 0)
            {
                throw new InvalidOperationException($"no memory for the hook ({Marshal.GetLastPInvokeError()})");
            }
        }

        var cave = (uint)_buffer;
        var scriptAddress = cave + 0x100;
        var code = new List<byte>();
        code.AddRange(Original[..6]);                                 // mov eax, [0xD213BC]; push eax
        AddRelative(code, 0xE8, cave, RunCommandFunctions);           // call run_command_functions (the room's)
        code.AddRange([0x83, 0xC4, 0x04]);                            // add esp, 4
        code.Add(0x68);                                               // push our script
        code.AddRange(BitConverter.GetBytes(scriptAddress));
        AddRelative(code, 0xE8, cave, RunCommandFunctions);           // call run_command_functions (ours)
        code.AddRange([0x83, 0xC4, 0x04]);                            // add esp, 4
        AddRelative(code, 0xE9, cave, ReturnTo);                      // jmp back

        if (ProcessSuspender.Suspend(_processId) is { } error)
        {
            throw new InvalidOperationException("suspend failed: " + error);
        }

        try
        {
            Write(scriptAddress, script);
            Write(cave, [.. code]);
            if (!_applied)
            {
                if (!Read(Site, Original.Length).AsSpan().SequenceEqual(Original))
                {
                    throw new InvalidOperationException($"unexpected code at 0x{Site:X}: not the build this was checked on");
                }

                var jump = new List<byte>();
                AddRelative(jump, 0xE9, Site, cave);
                jump.AddRange(Enumerable.Repeat((byte)0x90, Original.Length - 5));
                WriteCode(Site, [.. jump]);
                _applied = true;
            }
        }
        finally
        {
            ProcessSuspender.Resume(_processId);
        }
    }

    /// <summary>Puts the original call back.</summary>
    public void Remove()
    {
        if (!_applied)
        {
            return;
        }

        if (ProcessSuspender.Suspend(_processId) is { } error)
        {
            throw new InvalidOperationException("suspend failed: " + error);
        }

        try
        {
            WriteCode(Site, Original);
            _applied = false;
        }
        finally
        {
            ProcessSuspender.Resume(_processId);
        }
    }

    public void Dispose()
    {
        try
        {
            Remove();
        }
        finally
        {
            _process.Dispose();
        }
    }

    // A call or jump at code.Count within the block at `from` (the instruction's own address) to `target`.
    private static void AddRelative(List<byte> code, byte opcode, uint from, uint target)
    {
        var at = from + (uint)code.Count;
        code.Add(opcode);
        code.AddRange(BitConverter.GetBytes(unchecked((int)(target - (at + 5)))));
    }

    private void WriteCode(uint address, byte[] bytes)
    {
        if (!VirtualProtectEx(_process, (nint)address, (nuint)bytes.Length, 0x40, out var old))
        {
            throw new InvalidOperationException($"VirtualProtectEx 0x{address:X} failed ({Marshal.GetLastPInvokeError()})");
        }

        try
        {
            Write(address, bytes);
            FlushInstructionCache(_process, (nint)address, (nuint)bytes.Length);
        }
        finally
        {
            VirtualProtectEx(_process, (nint)address, (nuint)bytes.Length, old, out _);
        }
    }

    private byte[] Read(uint address, int length)
    {
        var buffer = new byte[length];
        if (!ReadProcessMemory(_process, (nint)address, buffer, (nuint)length, out var read) || read != (nuint)length)
        {
            throw new InvalidOperationException($"read 0x{address:X} failed");
        }

        return buffer;
    }

    private void Write(uint address, byte[] bytes)
    {
        if (!WriteProcessMemory(_process, (nint)address, bytes, (nuint)bytes.Length, out var written) || written != (nuint)bytes.Length
            || !Read(address, bytes.Length).AsSpan().SequenceEqual(bytes))
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

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool VirtualProtectEx(SafeProcessHandle process, nint address, nuint size, uint protect, out uint old);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint VirtualAllocEx(SafeProcessHandle process, nint address, nuint size, uint allocationType, uint protect);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlushInstructionCache(SafeProcessHandle process, nint address, nuint size);
}
