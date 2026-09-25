using FluentAssertions;
using Xunit;

namespace GlobalGraffitiWall.Tests;

public class CoordinateMathTests
{
    private const int WorldWidth = 10000;
    private const int WorldHeight = 10000;
    private const int TileSize = 256;
    private const int MinimapDimension = 160;

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(9999, 0, 9999)]
    [InlineData(0, 1, 10000)]
    [InlineData(500, 500, 500 * 10000 + 500)]
    [InlineData(9999, 9999, 9999 * 10000 + 9999)]
    public void Calculate8BitBufferIndex_ShouldMatchRowMajorFormula(int x, int y, long expectedIndex)
    {
        // Act: 8-bit continuous array index for a 10,000x10,000 canvas
        long actualIndex = ((long)y * WorldWidth) + x;

        // Assert
        actualIndex.Should().Be(expectedIndex);
        actualIndex.Should().BeInRange(0, 99_999_999);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(10000, 0)]
    [InlineData(0, 10000)]
    [InlineData(15000, 15000)]
    public void CoordinateBoundsCheck_ShouldDetectOutOfBounds(int x, int y)
    {
        // Act
        bool isWithinBounds = x >= 0 && x < WorldWidth && y >= 0 && y < WorldHeight;

        // Assert
        isWithinBounds.Should().BeFalse();
    }

    [Theory]
    [InlineData(0, 0, 0, 0, 0)]
    [InlineData(5000, 5000, 80, 80, 80 * 160 + 80)]
    [InlineData(9999, 9999, 159, 159, 159 * 160 + 159)]
    public void MinimapCoordinateMapping_ShouldMap10000SpaceTo160Space(int x, int y, int expectedMx, int expectedMy, int expectedMIndex)
    {
        // Act: Formula used in CanvasInitializerService and UpdateRedisPixelBufferAsync
        int mx = Math.Clamp((int)(x * 160.0 / WorldWidth), 0, MinimapDimension - 1);
        int my = Math.Clamp((int)(y * 160.0 / WorldHeight), 0, MinimapDimension - 1);
        int mIndex = (my * MinimapDimension) + mx;

        // Assert
        mx.Should().Be(expectedMx);
        my.Should().Be(expectedMy);
        mIndex.Should().Be(expectedMIndex);
        mIndex.Should().BeInRange(0, 25599); // 160 * 160 = 25,600 bytes
    }

    [Fact]
    public void TileChunkGrid_ShouldCoverEntireWorldWith40TilesPerDimension()
    {
        // Act
        int tilesPerRow = (int)Math.Ceiling((double)WorldWidth / TileSize);
        int totalTiles = tilesPerRow * tilesPerRow;

        // Assert
        tilesPerRow.Should().Be(40);
        totalTiles.Should().Be(1600);
    }

    [Theory]
    [InlineData(0, 0, 0, 255, 0, 255)]
    [InlineData(1, 1, 256, 511, 256, 511)]
    [InlineData(39, 39, 9984, 9999, 9984, 9999)]
    public void TileChunkBounds_ShouldCalculateCorrectPixelRanges(int tx, int ty, int expectedX1, int expectedX2, int expectedY1, int expectedY2)
    {
        // Act
        int x1 = tx * TileSize;
        int x2 = Math.Min(WorldWidth, (tx + 1) * TileSize) - 1;
        int y1 = ty * TileSize;
        int y2 = Math.Min(WorldHeight, (ty + 1) * TileSize) - 1;

        // Assert
        x1.Should().Be(expectedX1);
        x2.Should().Be(expectedX2);
        y1.Should().Be(expectedY1);
        y2.Should().Be(expectedY2);
    }
}
