using System.Buffers.Binary;
using System.IO.Compression;

namespace ShaderLab.Imaging;

/// <summary>
/// Minimal PNG-leser for <c>--compare</c>: 8-bit RGB eller RGBA, uten interlacing, alle fem filtertyper.
/// Det dekker det PngWriter skriver og det vanlige verktøy lagrer. Returnerer RGB24 (alfa forkastes).
/// </summary>
public static class PngReader
{
    public static (byte[] Rgb24, int Width, int Height) Read(string path)
    {
        byte[] file = File.ReadAllBytes(path);
        if (file.Length < 8 || !file.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
            throw new InvalidDataException($"'{path}' er ikke en PNG-fil.");

        int width = 0, height = 0, channels = 0;
        using var idat = new MemoryStream();
        for (int pos = 8; pos + 8 <= file.Length;)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(pos));
            string type = System.Text.Encoding.ASCII.GetString(file, pos + 4, 4);
            var data = file.AsSpan(pos + 8, length);
            switch (type)
            {
                case "IHDR":
                    width = BinaryPrimitives.ReadInt32BigEndian(data);
                    height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                    byte depth = data[8], colorType = data[9], interlace = data[12];
                    channels = colorType switch { 2 => 3, 6 => 4, _ => 0 };
                    if (depth != 8 || channels == 0 || interlace != 0)
                        throw new NotSupportedException(
                            $"'{path}': bare 8-bit RGB/RGBA uten interlacing støttes (bitdybde {depth}, fargetype {colorType}, interlace {interlace}).");
                    break;
                case "IDAT":
                    idat.Write(data);
                    break;
                case "IEND":
                    pos = file.Length;
                    continue;
            }
            pos += 12 + length;
        }
        if (width == 0) throw new InvalidDataException($"'{path}' mangler IHDR.");

        idat.Position = 0;
        using var z = new ZLibStream(idat, CompressionMode.Decompress);
        int stride = width * channels;
        var raw = new byte[(stride + 1) * height];
        z.ReadExactly(raw);

        var rgb = new byte[width * height * 3];
        var prev = new byte[stride];
        var row = new byte[stride];
        for (int y = 0; y < height; y++)
        {
            byte filter = raw[y * (stride + 1)];
            raw.AsSpan(y * (stride + 1) + 1, stride).CopyTo(row);
            Unfilter(filter, row, prev, channels);
            for (int x = 0; x < width; x++)
            {
                rgb[(y * width + x) * 3 + 0] = row[x * channels + 0];
                rgb[(y * width + x) * 3 + 1] = row[x * channels + 1];
                rgb[(y * width + x) * 3 + 2] = row[x * channels + 2];
            }
            (prev, row) = (row, prev);
        }
        return (rgb, width, height);
    }

    private static void Unfilter(byte filter, byte[] row, byte[] prev, int bpp)
    {
        for (int i = 0; i < row.Length; i++)
        {
            int a = i >= bpp ? row[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
            row[i] = (byte)(row[i] + filter switch
            {
                0 => 0,
                1 => a,
                2 => b,
                3 => (a + b) / 2,
                4 => Paeth(a, b, c),
                _ => throw new InvalidDataException($"Ukjent PNG-filtertype {filter}.")
            });
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
