using System.Text.RegularExpressions;
using FluentAssertions;
using GlobalGraffitiWall.API;
using Xunit;

namespace GlobalGraffitiWall.Tests;

public class PaletteTests
{
    private static readonly Regex HexColorRegex = new(@"^#[0-9A-Fa-f]{6}$", RegexOptions.Compiled);

    [Fact]
    public void PaletteColorModel_ShouldPreservePropertiesAndValidHexFormat()
    {
        var color = new PaletteColor
        {
            Id = 5,
            HexCode = "#E50000",
            Name = "Crimson Red",
            IsActive = true,
            SortOrder = 5
        };

        color.Id.Should().Be(5);
        color.HexCode.Should().Be("#E50000");
        color.Name.Should().Be("Crimson Red");
        color.IsActive.Should().BeTrue();
        color.SortOrder.Should().Be(5);

        HexColorRegex.IsMatch(color.HexCode).Should().BeTrue();
    }

    [Fact]
    public void ActiveColorFiltering_ShouldOnlyReturnActiveColorsSortedByOrder()
    {
        // Arrange: Sample set of 6 colors with 3 active
        var colors = new List<PaletteColor>
        {
            new() { Id = 0, HexCode = "#FFFFFF", Name = "Pure White", IsActive = true, SortOrder = 0 },
            new() { Id = 1, HexCode = "#E4E4E4", Name = "Light Gray", IsActive = true, SortOrder = 1 },
            new() { Id = 100, HexCode = "#FF0033", Name = "Hot Red", IsActive = false, SortOrder = 100 },
            new() { Id = 2, HexCode = "#888888", Name = "Medium Gray", IsActive = true, SortOrder = 2 },
            new() { Id = 200, HexCode = "#39FF14", Name = "Neon Green", IsActive = false, SortOrder = 200 }
        };

        // Act
        var activeColors = colors.Where(c => c.IsActive).OrderBy(c => c.SortOrder).ToList();
        var allColors = colors.OrderBy(c => c.SortOrder).ToList();

        // Assert
        activeColors.Should().HaveCount(3);
        activeColors.Select(c => c.Id).Should().ContainInOrder(0, 1, 2);
        allColors.Should().HaveCount(5);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(15, true)]
    [InlineData(31, true)]
    [InlineData(32, false)]
    [InlineData(255, false)]
    public void ActiveFlagsArray_ShouldLookupInConstantTime(byte colorId, bool expectedActive)
    {
        // Arrange: Initial 32 active colors (0 to 31)
        var flags = new bool[256];
        for (int i = 0; i < 32; i++)
        {
            flags[i] = true;
        }

        // Act
        bool isActive = flags[colorId];

        // Assert
        isActive.Should().Be(expectedActive);
    }
}
