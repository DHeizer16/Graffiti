using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace GlobalGraffitiWall.API;

/// <summary>
/// High-performance, zero-dependency PNG encoder that produces 8-bit indexed PNG images
/// from raw byte arrays using the 256-color palette.
/// </summary>
public static class PngEncoder
{
    private static readonly byte[] PngHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] CrcTable = InitializeCrcTable();

    private static uint[] InitializeCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xedb88320u ^ (c >> 1) : c >> 1;
            }
            table[i] = c;
        }
        return table;
    }

    /// <summary>
    /// Computes the IEEE 802.3 CRC32 checksum over the provided chunk type and chunk data buffers.
    /// </summary>
    public static uint ComputeCrc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        uint c = 0xffffffffu;
        foreach (byte b in type)
        {
            c = CrcTable[(c ^ b) & 0xff] ^ (c >> 8);
        }
        foreach (byte b in data)
        {
            c = CrcTable[(c ^ b) & 0xff] ^ (c >> 8);
        }
        return c ^ 0xffffffffu;
    }

    /// <summary>
    /// Encodes an 8-bit indexed pixel buffer into a valid PNG byte stream using the provided palette.
    /// Supports nearest-neighbor integer scaling (1x to 16x).
    /// </summary>
    public static byte[] EncodeIndexedPng(
        ReadOnlySpan<byte> pixelData,
        int width,
        int height,
        IReadOnlyList<PaletteColor> palette,
        int scale = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        scale = Math.Clamp(scale, 1, 16);

        if (pixelData.Length < width * height)
        {
            throw new ArgumentException($"Pixel data length ({pixelData.Length}) is smaller than width * height ({width * height}).");
        }

        int outputWidth = checked(width * scale);
        int outputHeight = checked(height * scale);

        // 1. Build 256-color palette RGB table (768 bytes: 256 x 3 bytes RGB)
        byte[] paletteRgb = new byte[768];
        // Default to white for unassigned entries
        paletteRgb.AsSpan().Fill(255);

        if (palette != null)
        {
            foreach (var c in palette)
            {
                if (TryParseHexRgb(c.HexCode, out byte r, out byte g, out byte b))
                {
                    int offset = c.Id * 3;
                    paletteRgb[offset] = r;
                    paletteRgb[offset + 1] = g;
                    paletteRgb[offset + 2] = b;
                }
            }
        }

        // 2. Generate raw scanlines with PNG filter byte 0 (None)
        // Each output row consists of 1 filter byte (0) + outputWidth indexed pixel bytes.
        int scanlineLength = checked(1 + outputWidth);
        long totalRawBytes = (long)scanlineLength * outputHeight;
        if (totalRawBytes > 64_000_000)
        {
            throw new InvalidOperationException($"Output image scanlines ({totalRawBytes} bytes) exceed maximum allowed size.");
        }

        byte[] rawScanlines = new byte[totalRawBytes];

        for (int y = 0; y < height; y++)
        {
            int sourceRowOffset = y * width;
            for (int sY = 0; sY < scale; sY++)
            {
                int outRow = (y * scale) + sY;
                int outRowOffset = outRow * scanlineLength;
                rawScanlines[outRowOffset] = 0; // Filter byte 0 (None)

                int targetCol = outRowOffset + 1;
                for (int x = 0; x < width; x++)
                {
                    byte colorIndex = pixelData[sourceRowOffset + x];
                    for (int sX = 0; sX < scale; sX++)
                    {
                        rawScanlines[targetCol++] = colorIndex;
                    }
                }
            }
        }

        // 3. Compress scanlines using ZLibStream (RFC 1950 zlib wrapper)
        byte[] compressedIdat;
        using (var msComp = new MemoryStream())
        {
            using (var zlib = new ZLibStream(msComp, CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(rawScanlines, 0, rawScanlines.Length);
            }
            compressedIdat = msComp.ToArray();
        }

        // 4. Assemble PNG Chunks: Signature + IHDR + PLTE + IDAT + IEND
        using var outputStream = new MemoryStream();
        outputStream.Write(PngHeader);

        // IHDR Chunk: Width (4), Height (4), BitDepth (1), ColorType (1), CompMethod (1), FilterMethod (1), InterlaceMethod (1)
        byte[] ihdrData = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdrData.AsSpan(0, 4), outputWidth);
        BinaryPrimitives.WriteInt32BigEndian(ihdrData.AsSpan(4, 4), outputHeight);
        ihdrData[8] = 8; // 8 bits per sample
        ihdrData[9] = 3; // Color Type 3 = Indexed-color
        ihdrData[10] = 0; // Compression method 0 (deflate)
        ihdrData[11] = 0; // Filter method 0
        ihdrData[12] = 0; // Interlace method 0 (no interlace)
        WriteChunk(outputStream, "IHDR", ihdrData);

        // PLTE Chunk (768 bytes)
        WriteChunk(outputStream, "PLTE", paletteRgb);

        // IDAT Chunk
        WriteChunk(outputStream, "IDAT", compressedIdat);

        // IEND Chunk
        WriteChunk(outputStream, "IEND", ReadOnlySpan<byte>.Empty);

        return outputStream.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> lenBytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lenBytes, data.Length);
        stream.Write(lenBytes);

        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);

        if (!data.IsEmpty)
        {
            stream.Write(data);
        }

        uint crc = ComputeCrc32(typeBytes, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }

    public static bool TryParseHexRgb(string? hex, out byte r, out byte g, out byte b)
    {
        r = 255; g = 255; b = 255;
        if (string.IsNullOrWhiteSpace(hex)) return false;

        string clean = hex.Trim().TrimStart('#');
        if (clean.Length == 6 &&
            byte.TryParse(clean.AsSpan(0, 2), System.Globalization.NumberStyles.HexNumber, null, out r) &&
            byte.TryParse(clean.AsSpan(2, 2), System.Globalization.NumberStyles.HexNumber, null, out g) &&
            byte.TryParse(clean.AsSpan(4, 2), System.Globalization.NumberStyles.HexNumber, null, out b))
        {
            return true;
        }

        return false;
    }
}
