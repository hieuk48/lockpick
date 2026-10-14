using System.Runtime.InteropServices;
using Lockpick.Platform.Windows;
using Microsoft.Win32.SafeHandles;

namespace Lockpick.Autoplay;

/// <summary>
/// DEV RUNS ONLY. Enters a room the way the game's own doors do, for automated tests: it hands the game a door
/// record exactly like <c>door_begin_transition</c> (0x41B59C, disassembled on this build 2026-10-14), and the game
/// runs its normal fade, room load, camera and arrival placement:
/// <code>
///   [0xBEBCBC] u32 = record pointer      [0xBE41C0] u32 |= 0x02000000 (gameplay/transition)
///   [0xBEBCC0] u16 = 0 (message flags)   [0xBE961B] u8  = 1 (game loop 0x4810DE takes the room-change path)
/// </code>
/// The record is a real door's 24 bytes from a room file (the door opcode without its first two bytes): +0x0D
/// destination, +0x0B entry camera, +0x0E arrival x/y/z/angle — so the arrival spot is one the game itself uses.
/// Only these fixed fields are written, each is read back, and the game is suspended for the moment of the writes.
/// </summary>
internal sealed partial class RoomWarp : IDisposable
{
    public const int RecordLength = 24;
    private const ulong PendingDoorRecord = 0xBEBCBC;
    private const ulong StateFlags = 0xBE41C0;
    private const ulong MessageFlags = 0xBEBCC0;
    private const ulong TransitionByte = 0xBE961B;
    private const uint GameplayActive = 0x02000000;
    private const uint DoorTransition = 0x80;

    private readonly SafeProcessHandle _process;
    private readonly int _processId;
    private nint _record;

    public RoomWarp(int processId)
    {
        _processId = processId;
        // PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_QUERY_LIMITED_INFORMATION
        _process = OpenProcess(0x0008 | 0x0010 | 0x0020 | 0x1000, false, processId);
        if (_process.IsInvalid)
        {
            throw new InvalidOperationException($"cannot open the game (error {Marshal.GetLastPInvokeError()})");
        }
    }

    /// <summary>Starts the transition to the room the record leads to. The game must be in normal gameplay.</summary>
    public void Enter(byte[] record)
    {
        if (record.Length != RecordLength)
        {
            throw new ArgumentException($"a door record is {RecordLength} bytes");
        }

        if (_record == 0)
        {
            // One small buffer for the record, like the game's own room data holds its door records.
            _record = VirtualAllocEx(_process, 0, RecordLength, 0x3000, 0x04);
            if (_record == 0)
            {
                throw new InvalidOperationException($"no buffer for the door record ({Marshal.GetLastPInvokeError()})");
            }
        }

        if (ProcessSuspender.Suspend(_processId) is { } error)
        {
            throw new InvalidOperationException("suspend failed: " + error);
        }

        try
        {
            var flags = ReadU32(StateFlags);
            if ((flags & DoorTransition) != 0 || Read(TransitionByte, 1)[0] != 0)
            {
                throw new InvalidOperationException("the game is already changing rooms or in a menu");
            }

            Write((ulong)_record, record);
            Write(PendingDoorRecord, BitConverter.GetBytes((uint)_record));
            Write(MessageFlags, [0, 0]);
            Write(StateFlags, BitConverter.GetBytes(flags | GameplayActive));
            Write(TransitionByte, [1]);
        }
        finally
        {
            ProcessSuspender.Resume(_processId);
        }
    }

    public void Dispose() => _process.Dispose();

    private uint ReadU32(ulong address) => BitConverter.ToUInt32(Read(address, 4));

    private byte[] Read(ulong address, int length)
    {
        var buffer = new byte[length];
        if (!ReadProcessMemory(_process, (nint)address, buffer, (nuint)length, out var read) || read != (nuint)length)
        {
            throw new InvalidOperationException($"read 0x{address:X} failed ({Marshal.GetLastPInvokeError()})");
        }

        return buffer;
    }

    private void Write(ulong address, byte[] bytes)
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
    private static partial nint VirtualAllocEx(SafeProcessHandle process, nint address, nuint size, uint type, uint protect);
}
