using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using FluentAssertions;
using GlobalGraffitiWall.API;

namespace GlobalGraffitiWall.Tests;

public class PngEncoderTests
{
    private static readonly List<PaletteColor> TestPalette = new()
    {
        new PaletteColor { Id = 0, HexCode = "#FFFFFF", Name = "Pure White", IsActive = true, SortOrder = 0 },
        new PaletteColor { Id = 1, HexCode = "#000000", Name = "Pure Black", IsActive = true, SortOrder = 1 },
        new PaletteColor { Id = 2, HexCode = "#FF0000", Name = "Red", IsActive = true, SortOrder = 2 },
        new PaletteColor { Id = 3, HexCode = "#00FF00", Name = "Green", IsActive = true, SortOrder = 3 },
        new PaletteColor { Id = 4, HexCode = "#0000FF", Name = "Blue", IsActive = true, SortOrder = 4 },
    };

    [Fact]
    public void EncodeIndexedPng_ShouldStartWithValidPngSignature()
    {
        // Arrange
        byte[] pixels = [0, 1, 2, 3];
        byte[] expectedSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

        // Act
        byte[] png = PngEncoder.EncodeIndexedPng(pixels, 2, 2, TestPalette, scale: 1);

        // Assert
        png.Should().NotBeNull();
        png.Length.Should().BeGreaterThan(expectedSignature.Length);
        png.Take(8).Should().Equal(expectedSignature);
    }

    [Fact]
    public void EncodeIndexedPng_ShouldContainValidIhdrChunkWithCorrectDimensions()
    {
        // Arrange: 4x3 image
        int width = 4;
        int height = 3;
        byte[] pixels = new byte[width * height];

        // Act
        byte[] png = PngEncoder.EncodeIndexedPng(pixels, width, height, TestPalette, scale: 1);

        // Assert: IHDR chunk starts at byte 8 (after 8-byte signature)
        // [8..11] = length (13 bytes)
        int ihdrLength = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(8, 4));
        ihdrLength.Should().Be(13);

        // [12..15] = chunk type ("IHDR")
        string chunkType = Encoding.ASCII.GetString(png, 12, 4);
        chunkType.Should().Be("IHDR");

        // [16..19] = width, [20..23] = height
        int parsedWidth = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4));
        int parsedHeight = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4));
        parsedWidth.Should().Be(width);
        parsedHeight.Should().Be(height);

        // [24] = bit depth (8), [25] = color type (3 = indexed)
        png[24].Should().Be(8);
        png[25].Should().Be(3);
    }

    [Fact]
    public void EncodeIndexedPng_ShouldScaleDimensionsCorrectly()
    {
        // Arrange: 2x2 image scaled by 4x -> 8x8 image
        int width = 2;
        int height = 2;
        int scale = 4;
        byte[] pixels = [0, 1, 2, 3];

        // Act
        byte[] png = PngEncoder.EncodeIndexedPng(pixels, width, height, TestPalette, scale: scale);

        // Assert: IHDR dimensions should be 8x8
        int parsedWidth = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4));
        int parsedHeight = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4));
        parsedWidth.Should().Be(width * scale);
        parsedHeight.Should().Be(height * scale);
    }

    [Fact]
    public void EncodeIndexedPng_ShouldContainPlteChunkWithPaletteEntries()
    {
        // Arrange
        byte[] pixels = [0, 1];

        // Act
        byte[] png = PngEncoder.EncodeIndexedPng(pixels, 2, 1, TestPalette, scale: 1);

        // Assert: Find PLTE chunk
        // Signature = 8 bytes, IHDR = 4 (length) + 4 (type) + 13 (data) + 4 (crc) = 25 bytes.
        // Total offset to PLTE = 8 + 25 = 33
        int plteOffset = 33;
        int plteLength = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(plteOffset, 4));
        plteLength.Should().Be(768); // 256 colors * 3 bytes

        string plteType = Encoding.ASCII.GetString(png, plteOffset + 4, 4);
        plteType.Should().Be("PLTE");

        // Verify Color 0 (#FFFFFF) -> (255, 255, 255)
        int color0Offset = plteOffset + 8;
        png[color0Offset].Should().Be(255);
        png[color0Offset + 1].Should().Be(255);
        png[color0Offset + 2].Should().Be(255);

        // Verify Color 1 (#000000) -> (0, 0, 0)
        int color1Offset = color0Offset + 3;
        png[color1Offset].Should().Be(0);
        png[color1Offset + 1].Should().Be(0);
        png[color1Offset + 2].Should().Be(0);

        // Verify Color 2 (#FF0000) -> (255, 0, 0)
        int color2Offset = color0Offset + 6;
        png[color2Offset].Should().Be(255);
        png[color2Offset + 1].Should().Be(0);
        png[color2Offset + 2].Should().Be(0);
    }

    [Fact]
    public void EncodeIndexedPng_ShouldDecompressScanlinesMatchingInputPixels()
    {
        // Arrange: 2x2 image
        // Row 0: Color 2, Color 3
        // Row 1: Color 4, Color 1
        int width = 2;
        int height = 2;
        byte[] pixels = [2, 3, 4, 1];

        // Act
        byte[] png = PngEncoder.EncodeIndexedPng(pixels, width, height, TestPalette, scale: 1);

        // Find IDAT chunk
        // Offset: Signature (8) + IHDR (25) + PLTE (4 + 4 + 768 + 4 = 780) = 813
        int idatOffset = 813;
        int idatLength = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(idatOffset, 4));
        string idatType = Encoding.ASCII.GetString(png, idatOffset + 4, 4);
        idatType.Should().Be("IDAT");

        byte[] compressedData = png.AsSpan(idatOffset + 8, idatLength).ToArray();

        // Decompress with ZLibStream
        using var msCompressed = new MemoryStream(compressedData);
        using var zlib = new ZLibStream(msCompressed, CompressionMode.Decompress);
        using var msDecomp = new MemoryStream();
        zlib.CopyTo(msDecomp);
        byte[] decompressed = msDecomp.ToArray();

        // Scanline structure: each row has 1 filter byte (0) + width bytes
        // Total expected = height * (1 + width) = 2 * 3 = 6 bytes
        decompressed.Length.Should().Be(6);

        // Row 0: filter=0, pixel=2, pixel=3
        decompressed[0].Should().Be(0);
        decompressed[1].Should().Be(2);
        decompressed[2].Should().Be(3);

        // Row 1: filter=0, pixel=4, pixel=1
        decompressed[3].Should().Be(0);
        decompressed[4].Should().Be(4);
        decompressed[5].Should().Be(1);
    }

    [Fact]
    public void EncodeIndexedPng_ShouldEndWithValidIendChunk()
    {
        // Arrange
        byte[] pixels = [1];

        // Act
        byte[] png = PngEncoder.EncodeIndexedPng(pixels, 1, 1, TestPalette, scale: 1);

        // Assert: Last 12 bytes must be IEND chunk (length 0, type "IEND", crc)
        int iendOffset = png.Length - 12;
        int iendLength = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(iendOffset, 4));
        iendLength.Should().Be(0);

        string iendType = Encoding.ASCII.GetString(png, iendOffset + 4, 4);
        iendType.Should().Be("IEND");
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    [InlineData(-1, 5)]
    public void EncodeIndexedPng_ShouldThrowOnInvalidDimensions(int width, int height)
    {
        // Arrange & Act
        Action act = () => PngEncoder.EncodeIndexedPng(new byte[100], width, height, TestPalette);

        // Assert
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData("#FFFFFF", 255, 255, 255, true)]
    [InlineData("000000", 0, 0, 0, true)]
    [InlineData("#FF6B6B", 255, 107, 107, true)]
    [InlineData("invalid", 255, 255, 255, false)]
    [InlineData("", 255, 255, 255, false)]
    [InlineData(null, 255, 255, 255, false)]
    public void TryParseHexRgb_ShouldParseColorsCorrectly(string? hex, byte expectedR, byte expectedG, byte expectedB, bool expectedSuccess)
    {
        // Act
        bool success = PngEncoder.TryParseHexRgb(hex, out byte r, out byte g, out byte b);

        // Assert
        success.Should().Be(expectedSuccess);
        r.Should().Be(expectedR);
        g.Should().Be(expectedG);
        b.Should().Be(expectedB);
    }
}
