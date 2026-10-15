using System.Buffers.Binary;

namespace Lockpick.Core.Memory;

/// <summary>Read-only access to another process's address space.</summary>
public interface IProcessMemory
{
    /// <summary>
    /// Reads exactly <paramref name="destination"/>.Length bytes at <paramref name="address"/>.
    /// Returns false (never throws) when the read fails or is partial.
    /// </summary>
    bool TryRead(ulong address, Span<byte> destination);
}

/// <summary>A contiguous address range [Start, Start + Length).</summary>
public readonly record struct AddressRange(ulong Start, ulong Length)
{
    public ulong End => Start + Length;

    public bool Contains(ulong address, int size) =>
        size >= 0 && address >= Start && address + (ulong)size <= End && address + (ulong)size >= address;

    public override string ToString() => $"0x{Start:X}-0x{End:X}";
}

/// <summary>
/// Decorator that rejects any read outside an explicit allow-list of ranges (normally the
/// verified game module image). Guards against corrupt profiles or pointer values.
/// </summary>
public sealed class ValidatedMemory(IProcessMemory inner, IReadOnlyList<AddressRange> allowed) : IProcessMemory
{
    public IReadOnlyList<AddressRange> AllowedRanges { get; } = allowed;

    public long RejectedReads { get; private set; }

    public bool IsAllowed(ulong address, int size)
    {
        foreach (var range in AllowedRanges)
        {
            if (range.Contains(address, size))
            {
                return true;
            }
        }

        return false;
    }

    public bool TryRead(ulong address, Span<byte> destination)
    {
        if (!IsAllowed(address, destination.Length))
        {
            RejectedReads++;
            return false;
        }

        return inner.TryRead(address, destination);
    }
}

/// <summary>Typed little-endian reads on top of <see cref="IProcessMemory"/>.</summary>
public static class ProcessMemoryExtensions
{
    public static bool TryReadU8(this IProcessMemory memory, ulong address, out byte value)
    {
        Span<byte> buffer = stackalloc byte[1];
        var ok = memory.TryRead(address, buffer);
        value = ok ? buffer[0] : (byte)0;
        return ok;
    }

    public static bool TryReadU16(this IProcessMemory memory, ulong address, out ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        var ok = memory.TryRead(address, buffer);
        value = ok ? BinaryPrimitives.ReadUInt16LittleEndian(buffer) : (ushort)0;
        return ok;
    }

    public static bool TryReadI16(this IProcessMemory memory, ulong address, out short value)
    {
        Span<byte> buffer = stackalloc byte[2];
        var ok = memory.TryRead(address, buffer);
        value = ok ? BinaryPrimitives.ReadInt16LittleEndian(buffer) : (short)0;
        return ok;
    }

    public static bool TryReadU32(this IProcessMemory memory, ulong address, out uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        var ok = memory.TryRead(address, buffer);
        value = ok ? BinaryPrimitives.ReadUInt32LittleEndian(buffer) : 0u;
        return ok;
    }

    public static bool TryReadI32(this IProcessMemory memory, ulong address, out int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        var ok = memory.TryRead(address, buffer);
        value = ok ? BinaryPrimitives.ReadInt32LittleEndian(buffer) : 0;
        return ok;
    }

    /// <summary>Reads a value of the given primitive type, sign- or zero-extended to 64 bits.</summary>
    public static bool TryReadValue(this IProcessMemory memory, ulong address, ValueType type, out long value)
    {
        bool ok;
        switch (type)
        {
            case ValueType.U8:
                ok = memory.TryReadU8(address, out var u8);
                value = u8;
                return ok;
            case ValueType.U16:
                ok = memory.TryReadU16(address, out var u16);
                value = u16;
                return ok;
            case ValueType.I16:
                ok = memory.TryReadI16(address, out var i16);
                value = i16;
                return ok;
            case ValueType.U32:
                ok = memory.TryReadU32(address, out var u32);
                value = u32;
                return ok;
            case ValueType.I32:
                ok = memory.TryReadI32(address, out var i32);
                value = i32;
                return ok;
            default:
                throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown value type.");
        }
    }
}

/// <summary>Primitive value types a profile field may declare.</summary>
public enum ValueType
{
    U8,
    U16,
    I16,
    U32,
    I32,
}

public static class ValueTypeExtensions
{
    /// <summary>Decodes a little-endian value of this type from the start of <paramref name="bytes"/>.</summary>
    public static long Decode(this ValueType type, ReadOnlySpan<byte> bytes) => type switch
    {
        ValueType.U8 => bytes[0],
        ValueType.U16 => BinaryPrimitives.ReadUInt16LittleEndian(bytes),
        ValueType.I16 => BinaryPrimitives.ReadInt16LittleEndian(bytes),
        ValueType.U32 => BinaryPrimitives.ReadUInt32LittleEndian(bytes),
        ValueType.I32 => BinaryPrimitives.ReadInt32LittleEndian(bytes),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown value type."),
    };

    /// <summary>The values the type can hold.</summary>
    public static (long Min, long Max) Range(this ValueType type) => type switch
    {
        ValueType.U8 => (byte.MinValue, byte.MaxValue),
        ValueType.U16 => (ushort.MinValue, ushort.MaxValue),
        ValueType.I16 => (short.MinValue, short.MaxValue),
        ValueType.U32 => (uint.MinValue, uint.MaxValue),
        ValueType.I32 => (int.MinValue, int.MaxValue),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown value type."),
    };

    /// <summary>Encodes <paramref name="value"/> little-endian into the start of <paramref name="bytes"/> (SizeOf bytes).</summary>
    public static void Encode(this ValueType type, long value, Span<byte> bytes)
    {
        switch (type)
        {
            case ValueType.U8:
                bytes[0] = checked((byte)value);
                break;
            case ValueType.U16:
                BinaryPrimitives.WriteUInt16LittleEndian(bytes, checked((ushort)value));
                break;
            case ValueType.I16:
                BinaryPrimitives.WriteInt16LittleEndian(bytes, checked((short)value));
                break;
            case ValueType.U32:
                BinaryPrimitives.WriteUInt32LittleEndian(bytes, checked((uint)value));
                break;
            case ValueType.I32:
                BinaryPrimitives.WriteInt32LittleEndian(bytes, checked((int)value));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown value type.");
        }
    }

    public static int SizeOf(this ValueType type) => type switch
    {
        ValueType.U8 => 1,
        ValueType.U16 or ValueType.I16 => 2,
        ValueType.U32 or ValueType.I32 => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown value type."),
    };
}
