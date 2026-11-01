using System.Buffers.Binary;
using System.IO.Compression;

namespace ShaderLab.Imaging;

/// <summary>Minimal 8-bit truecolor (RGB24) PNG encoder. No external dependencies -
/// keeps the published executable self-contained without pulling in GDI+/WIC.</summary>
public static class PngWriter
{
    private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>
    /// Writes to a temporary file next to it and swaps it in at the end, so an image viewer that reloads
    /// the file (--watch, --preview-every) never sees a half-written image.
    /// </summary>
    public static void Write(string path, byte[] rgb24, int width, int height, CompressionLevel level = CompressionLevel.Fastest)
    {
        int stride = width * 3;
        string temp = path + ".tmp";
        using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(Signature);
            WriteIhdr(fs, width, height);
            WriteIdat(fs, rgb24, stride, height, level);
            WriteChunk(fs, "IEND", ReadOnlySpan<byte>.Empty);
        }
        File.Move(temp, path, overwrite: true);
    }

    private static void WriteIhdr(Stream fs, int width, int height)
    {
        Span<byte> data = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(data[0..4], width);
        BinaryPrimitives.WriteInt32BigEndian(data[4..8], height);
        data[8] = 8;  // bit depth
        data[9] = 2;  // color type: truecolor (RGB)
        data[10] = 0; // compression method
        data[11] = 0; // filter method
        data[12] = 0; // interlace method
        WriteChunk(fs, "IHDR", data);
    }

    private static void WriteIdat(Stream fs, byte[] rgb24, int stride, int height, CompressionLevel level)
    {
        using var raw = new MemoryStream(stride * height + height);
        // Filter type 0 (None) per scanline: cheapest to produce, favors throughput
        // over file size.
        for (int y = 0; y < height; y++)
        {
            raw.WriteByte(0);
            raw.Write(rgb24, y * stride, stride);
        }
        raw.Position = 0;

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, level, leaveOpen: true))
        {
            raw.CopyTo(zlib);
        }

        WriteChunk(fs, "IDAT", compressed.ToArray());
    }

    private static void WriteChunk(Stream fs, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> lenBuf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lenBuf, data.Length);
        fs.Write(lenBuf);

        Span<byte> typeBytes = stackalloc byte[4];
        typeBytes[0] = (byte)type[0];
        typeBytes[1] = (byte)type[1];
        typeBytes[2] = (byte)type[2];
        typeBytes[3] = (byte)type[3];
        fs.Write(typeBytes);
        fs.Write(data);

        uint crc = Crc32.Initial;
        crc = Crc32.Update(typeBytes, crc);
        crc = Crc32.Update(data, crc);
        crc = Crc32.Finish(crc);
        Span<byte> crcBuf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBuf, crc);
        fs.Write(crcBuf);
    }
}

internal static class Crc32
{
    public const uint Initial = 0xFFFFFFFF;
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    public static uint Update(ReadOnlySpan<byte> data, uint crc)
    {
        foreach (byte b in data)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    public static uint Finish(uint crc) => ~crc;
}
