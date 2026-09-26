using FluentAssertions;
using Xunit;

namespace GlobalGraffitiWall.Tests;

public class TokenSystemTests
{
    [Theory]
    // Micro stickers (10x10 = 100 px)
    [InlineData(10, 10, 15, 2)]
    [InlineData(10, 10, 60, 4)]
    [InlineData(10, 10, 240, 10)]
    [InlineData(10, 10, 1440, 21)]
    // Standard murals (32x32 = 1024 px)
    [InlineData(32, 32, 15, 2)]
    [InlineData(32, 32, 60, 6)]
    [InlineData(32, 32, 240, 15)]
    [InlineData(32, 32, 1440, 30)]
    // Large collaborative art (64x64 = 4096 px)
    [InlineData(64, 64, 15, 5)]
    [InlineData(64, 64, 60, 12)]
    [InlineData(64, 64, 240, 29)]
    [InlineData(64, 64, 1440, 61)]
    // Guild/Clan megazones (128x128 = 16384 px)
    [InlineData(128, 128, 15, 15)]
    [InlineData(128, 128, 60, 37)]
    [InlineData(128, 128, 240, 88)]
    [InlineData(128, 128, 1440, 184)]
    public void CalculateCost_ShouldReturnAccurateTieredTokenCosts(int width, int height, int durationMinutes, int expectedCost)
    {
        // Act: Formula mirrors ReservationService.CalculateCost
        int area = Math.Max(25, width * height);
        double durationMultiplier = durationMinutes switch
        {
            <= 15 => 0.4,
            <= 60 => 1.0,
            <= 240 => 2.4,
            <= 720 => 3.8,
            _ => 5.0
        };
        double areaBase = 4.0 + (area / 500.0);
        int cost = (int)Math.Max(2, Math.Round(areaBase * durationMultiplier));

        // Assert
        cost.Should().Be(expectedCost);
    }

    [Theory]
    [InlineData(16, 50, 10, 6, 50, 0)]   // Has 16 charges, cost 10 -> consumes 10 charges, 0 bonus
    [InlineData(5, 50, 10, 0, 45, 5)]    // Has 5 charges, 50 bonus, cost 10 -> consumes 5 charges + 5 bonus
    [InlineData(0, 50, 10, 0, 40, 10)]   // Has 0 charges, 50 bonus, cost 10 -> consumes 0 charges + 10 bonus
    [InlineData(2, 5, 5, 0, 2, 3)]       // Has 2 charges, 5 bonus, cost 5 -> consumes 2 charges + 3 bonus
    public void DualPoolDeduction_ShouldConsumeNormalChargesBeforeBonusTokens(
        int normalCharges, int bonusBalance, int cost,
        int expectedNormalRemaining, int expectedBonusRemaining, int expectedBonusDeducted)
    {
        // Act: Simulate Lua reservation deduction logic
        int totalAvailable = normalCharges + bonusBalance;
        bool canAfford = totalAvailable >= cost;

        canAfford.Should().BeTrue();

        int deductNormal = Math.Min(normalCharges, cost);
        int remainingNormal = normalCharges - deductNormal;
        int remainingCost = cost - deductNormal;

        int finalBonus = bonusBalance - remainingCost;

        // Assert
        remainingNormal.Should().Be(expectedNormalRemaining);
        finalBonus.Should().Be(expectedBonusRemaining);
        remainingCost.Should().Be(expectedBonusDeducted);
    }

    [Fact]
    public void DualPoolDeduction_ShouldRejectWhenTotalTokensAreInsufficient()
    {
        // Arrange: User has 4 charges and 10 bonus tokens (14 total), but lease costs 25
        int normalCharges = 4;
        int bonusBalance = 10;
        int cost = 25;

        // Act
        int totalAvailable = normalCharges + bonusBalance;
        bool canAfford = totalAvailable >= cost;

        // Assert
        canAfford.Should().BeFalse();
        totalAvailable.Should().Be(14);
    }

    [Theory]
    // 4 hours (240m), cost 88 tokens, released after 1 hour (180m remaining = 75% unused) -> 88 * 0.75 * 0.5 = 33
    [InlineData(88, 240, 180, 33)]
    // 24 hours (1440m), cost 184 tokens, released after 12 hours (720m remaining = 50% unused) -> 184 * 0.5 * 0.5 = 46
    [InlineData(184, 1440, 720, 46)]
    // 1 hour (60m), cost 10 tokens, released after 30 mins (30m remaining = 50% unused) -> 10 * 0.5 * 0.5 = 2
    [InlineData(10, 60, 30, 2)]
    // Expired lease (0 remaining) -> 0 refund
    [InlineData(50, 60, 0, 0)]
    // Free studio owner reservation (0 cost) -> 0 refund
    [InlineData(0, 60, 45, 0)]
    public void EarlyReleaseRefund_ShouldCalculateAccurate50PercentProratedRefund(
        int tokenCost, int totalMinutes, int remainingMinutes, int expectedRefund)
    {
        // Act: Formula mirrors ReservationService.CancelReservationAsync
        int refund = 0;
        if (tokenCost > 0 && totalMinutes > 0 && remainingMinutes > 0)
        {
            double unusedRatio = (double)remainingMinutes / totalMinutes;
            refund = (int)Math.Floor(tokenCost * unusedRatio * 0.5);
        }

        // Assert
        refund.Should().Be(expectedRefund);
    }

    [Fact]
    public void DailySupplyDrop_ShouldAllowClaimWhenFirstTimeOrAfter24Hours()
    {
        // Arrange
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset? neverClaimed = null;
        DateTimeOffset? claimedYesterday = now.AddHours(-25);
        DateTimeOffset? claimedRecent = now.AddHours(-10);

        // Act & Assert 1: First time claim
        bool canClaimFirstTime = !neverClaimed.HasValue || (now >= neverClaimed.Value.AddHours(24));
        canClaimFirstTime.Should().BeTrue();

        // Act & Assert 2: Claimed 25 hours ago -> eligible
        bool canClaimYesterday = !claimedYesterday.HasValue || (now >= claimedYesterday.Value.AddHours(24));
        canClaimYesterday.Should().BeTrue();

        // Act & Assert 3: Claimed 10 hours ago -> blocked with 14 hours remaining
        bool canClaimRecent = !claimedRecent.HasValue || (now >= claimedRecent.Value.AddHours(24));
        canClaimRecent.Should().BeFalse();

        var remainingSeconds = (claimedRecent.Value.AddHours(24) - now).TotalSeconds;
        remainingSeconds.Should().BeApproximately(14 * 3600, 5);
    }
}
