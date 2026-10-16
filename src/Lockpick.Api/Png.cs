using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Lockpick.Api;

/// <summary>A minimal PNG writer (RGBA, 8 bits) for the overlay pictures the API serves; no image library needed.</summary>
public static class Png
{
    private static readonly byte[] Signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] CrcTable = MakeCrcTable();

    /// <summary>Encodes premultiplied BGRA pixels (what Lockpick's overlay draws) as a PNG with straight alpha.</summary>
    public static byte[] FromPremultipliedBgra(byte[] bgra, int width, int height)
    {
        var raw = new byte[height * (1 + width * 4)];
        for (var y = 0; y < height; y++)
        {
            var row = y * (1 + width * 4);
            raw[row] = 0; // filter: none
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                var o = row + 1 + x * 4;
                var a = bgra[i + 3];
                raw[o] = Straight(bgra[i + 2], a);
                raw[o + 1] = Straight(bgra[i + 1], a);
                raw[o + 2] = Straight(bgra[i], a);
                raw[o + 3] = a;
            }
        }

        using var output = new MemoryStream();
        output.Write(Signature);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // bit depth
        header[9] = 6; // colour type: RGBA
        Chunk(output, "IHDR", header);
        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            {
                zlib.Write(raw);
            }

            Chunk(output, "IDAT", compressed.ToArray());
        }

        Chunk(output, "IEND", []);
        return output.ToArray();
    }

    private static byte Straight(byte premultiplied, byte alpha) =>
        alpha == 0 ? (byte)0 : (byte)Math.Min(255, premultiplied * 255 / alpha);

    private static void Chunk(Stream output, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);
        var crc = Crc(Crc(0xFFFFFFFFu, typeBytes), data) ^ 0xFFFFFFFFu;
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }

    private static uint Crc(uint crc, byte[] data)
    {
        foreach (var b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] MakeCrcTable()
    {
        var table = new uint[256];
        for (var n = 0u; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
