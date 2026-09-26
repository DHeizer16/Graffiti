using FluentAssertions;
using GlobalGraffitiWall.API;
using Xunit;

namespace GlobalGraffitiWall.Tests;

public class CursorPresenceTests
{
    private const int ZoneSize = 500;
    private const int CanvasDimension = 10000;

    [Fact]
    public void CanvasHub_ZoneSize_ShouldBe500()
    {
        CanvasHub.ZoneSize.Should().Be(500);
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(250, 250, 0, 0)]
    [InlineData(499, 499, 0, 0)]
    [InlineData(500, 500, 1, 1)]
    [InlineData(501, 750, 1, 1)]
    [InlineData(1000, 1500, 2, 3)]
    [InlineData(9999, 9999, 19, 19)]
    public void Coordinate_To_Zone_Mapping_CalculatesCorrectly(int x, int y, int expectedZx, int expectedZy)
    {
        int zx = x / ZoneSize;
        int zy = y / ZoneSize;

        zx.Should().Be(expectedZx);
        zy.Should().Be(expectedZy);
    }

    [Fact]
    public void GlobalWall_CursorGroupName_ShouldFollowConvention()
    {
        string group = CanvasHub.GetCursorZoneGroupName(null, 3, 7);
        group.Should().Be("cursor:global:3_7");

        string emptyGroup = CanvasHub.GetCursorZoneGroupName(string.Empty, 0, 0);
        emptyGroup.Should().Be("cursor:global:0_0");
    }

    [Fact]
    public void PrivateWall_CursorGroupName_ShouldIncludeWallId()
    {
        var wallId = Guid.NewGuid();
        string group = CanvasHub.GetCursorZoneGroupName(wallId.ToString(), 1, 2);
        group.Should().Be($"cursor:{wallId}:1_2");
    }

    [Fact]
    public void Viewport_OverlappingZones_CalculatesAllTouchingZones()
    {
        // Viewport spanning x: 450..550, y: 480..520 (crosses x=500 and y=500 boundary)
        int vx1 = 450, vy1 = 480;
        int vx2 = 550, vy2 = 520;

        int minZx = Math.Max(0, vx1 / ZoneSize);
        int maxZx = Math.Min((CanvasDimension - 1) / ZoneSize, vx2 / ZoneSize);
        int minZy = Math.Max(0, vy1 / ZoneSize);
        int maxZy = Math.Min((CanvasDimension - 1) / ZoneSize, vy2 / ZoneSize);

        var zones = new List<string>();
        for (int x = minZx; x <= maxZx; x++)
        {
            for (int y = minZy; y <= maxZy; y++)
            {
                zones.Add($"{x}_{y}");
            }
        }

        // Should touch 4 zones: 0_0, 0_1, 1_0, 1_1
        zones.Should().HaveCount(4);
        zones.Should().Contain(["0_0", "0_1", "1_0", "1_1"]);
    }

    [Theory]
    [InlineData(100, 100, 101, 101, false)] // delta distance = sqrt(2) ≈ 1.41 < 2px deadband
    [InlineData(100, 100, 102, 100, true)]  // delta distance = 2.0 >= 2px deadband
    [InlineData(100, 100, 100, 103, true)]  // delta distance = 3.0 >= 2px deadband
    [InlineData(100, 100, 101, 102, true)]  // delta distance = sqrt(5) ≈ 2.23 >= 2px deadband
    public void CursorMovement_DeadbandCheck_EvaluatesCorrectly(int x1, int y1, int x2, int y2, bool shouldTransmit)
    {
        double distance = Math.Sqrt(Math.Pow(x2 - x1, 2) + Math.Pow(y2 - y1, 2));
        bool transmits = distance >= 2.0;

        transmits.Should().Be(shouldTransmit);
    }

    [Fact]
    public void TotalZones_OnGlobalWall_ShouldBe400()
    {
        int zonesPerAxis = CanvasDimension / ZoneSize;
        int totalZones = zonesPerAxis * zonesPerAxis;

        zonesPerAxis.Should().Be(20);
        totalZones.Should().Be(400);
    }
}
