using FluentAssertions;
using GlobalGraffitiWall.API;
using Xunit;

namespace GlobalGraffitiWall.Tests;

public class SpatialCollisionTests
{
    [Fact]
    public void Overlaps_IdenticalRectangles_ShouldReturnTrue()
    {
        var res = new CanvasReservation { X1 = 10, Y1 = 10, X2 = 50, Y2 = 50 };
        res.Overlaps(10, 10, 50, 50).Should().BeTrue();
    }

    [Fact]
    public void Overlaps_ContainedRectangle_ShouldReturnTrue()
    {
        var res = new CanvasReservation { X1 = 10, Y1 = 10, X2 = 100, Y2 = 100 };
        res.Overlaps(20, 20, 40, 40).Should().BeTrue();
    }

    [Fact]
    public void Overlaps_CornerOverlap_ShouldReturnTrue()
    {
        var res = new CanvasReservation { X1 = 50, Y1 = 50, X2 = 100, Y2 = 100 };
        res.Overlaps(20, 20, 55, 55).Should().BeTrue();
    }

    [Fact]
    public void Overlaps_TouchingEdge_ShouldReturnTrue()
    {
        var res = new CanvasReservation { X1 = 50, Y1 = 50, X2 = 100, Y2 = 100 };
        // Sharing edge at x = 100
        res.Overlaps(100, 50, 150, 100).Should().BeTrue();
    }

    [Theory]
    [InlineData(101, 50, 150, 100)] // Separated to the right
    [InlineData(0, 50, 49, 100)]    // Separated to the left
    [InlineData(50, 101, 100, 150)] // Separated below
    [InlineData(50, 0, 100, 49)]    // Separated above
    public void Overlaps_DisjointRectangles_ShouldReturnFalse(int ox1, int oy1, int ox2, int oy2)
    {
        var res = new CanvasReservation { X1 = 50, Y1 = 50, X2 = 100, Y2 = 100 };
        res.Overlaps(ox1, oy1, ox2, oy2).Should().BeFalse();
    }

    [Theory]
    [InlineData(50, 50, true)]   // Top-left corner
    [InlineData(100, 100, true)] // Bottom-right corner
    [InlineData(75, 75, true)]   // Center interior
    [InlineData(49, 75, false)]  // Just outside left
    [InlineData(101, 75, false)] // Just outside right
    [InlineData(75, 49, false)]  // Just outside top
    [InlineData(75, 101, false)] // Just outside bottom
    public void Contains_PointInReservation_ShouldAccuratelyDetermineHit(int px, int py, bool expectedInside)
    {
        var res = new CanvasReservation { X1 = 50, Y1 = 50, X2 = 100, Y2 = 100 };
        res.Contains(px, py).Should().Be(expectedInside);
    }

    [Fact]
    public void DimensionsAndArea_InclusiveBoundaries_ShouldComputeAccuratePixelCounts()
    {
        // A box from 10 to 14 is 5 pixels wide (10, 11, 12, 13, 14)
        var res = new CanvasReservation { X1 = 10, Y1 = 20, X2 = 14, Y2 = 29 };

        res.Width.Should().Be(5);
        res.Height.Should().Be(10);
        res.Area.Should().Be(50);
    }

    [Fact]
    public void ExpirationCheck_ShouldAccuratelyReflectTemporalStatus()
    {
        var now = DateTimeOffset.UtcNow;
        var activeRes = new CanvasReservation { ExpiresAt = now.AddMinutes(15) };
        var expiredRes = new CanvasReservation { ExpiresAt = now.AddMinutes(-1) };

        activeRes.IsExpired(now).Should().BeFalse();
        expiredRes.IsExpired(now).Should().BeTrue();
    }
}
