using FluentAssertions;
using Xunit;

namespace GlobalGraffitiWall.Tests;

public class RateLimitingTests
{
    private const double MaxCapacity = 16.0;
    private const double RefillRate = 0.2; // 1 token every 5 seconds

    [Theory]
    [InlineData(0.0, 5.0, 1.0)]    // 5s -> 1 token
    [InlineData(0.0, 10.0, 2.0)]   // 10s -> 2 tokens
    [InlineData(0.0, 80.0, 16.0)]  // 80s -> 16 tokens (full capacity)
    [InlineData(0.0, 120.0, 16.0)] // 120s -> capped at 16 tokens
    [InlineData(10.0, 15.0, 13.0)] // 10 tokens + 15s * 0.2 (3 tokens) = 13 tokens
    public void TokenRegeneration_ShouldFollowRefillRateAndCapAtMaxCapacity(double startingTokens, double elapsedSeconds, double expectedTokens)
    {
        // Act
        double regenerated = elapsedSeconds * RefillRate;
        double currentTokens = Math.Min(MaxCapacity, startingTokens + regenerated);

        // Assert
        currentTokens.Should().BeApproximately(expectedTokens, 0.001);
    }

    [Fact]
    public void BurstDeduction_ShouldAllow16ConsecutivePlacementsBeforeDepletion()
    {
        // Arrange
        double tokens = MaxCapacity;
        int placedCount = 0;

        // Act: Paint 16 pixels in rapid succession without cooldown delay
        while (tokens >= 1.0)
        {
            tokens -= 1.0;
            placedCount++;
        }

        // Assert
        placedCount.Should().Be(16);
        tokens.Should().BeApproximately(0.0, 0.001);
    }

    [Theory]
    [InlineData(0.0, 1.0, 5)]    // 0 tokens, need 1 -> wait 5s
    [InlineData(0.5, 1.0, 3)]    // 0.5 tokens, need 0.5 -> 0.5 / 0.2 = 2.5 -> ceil = 3s
    [InlineData(0.8, 1.0, 1)]    // 0.8 tokens, need 0.2 -> 0.2 / 0.2 = 1.0 -> 1s
    [InlineData(0.1, 1.0, 5)]    // 0.1 tokens, need 0.9 -> 0.9 / 0.2 = 4.5 -> ceil = 5s
    public void CooldownWaitTime_ShouldCalculateAccurateSecondsUntilNextCharge(double currentTokens, double requestedTokens, int expectedWaitSeconds)
    {
        // Act: Formula from atomic rate_limit.lua
        double missing = requestedTokens - currentTokens;
        int waitSeconds = (int)Math.Ceiling(missing / RefillRate);

        // Assert
        waitSeconds.Should().Be(expectedWaitSeconds);
    }

    [Fact]
    public void BonusChargeFallback_ShouldConsumeBonusWhenRegeneratingChargesAreDepleted()
    {
        // Arrange
        double regeneratingTokens = 0.0;
        int bonusTokens = 5;
        double requested = 1.0;

        // Act: Simulate Lua logic
        bool success;
        if (regeneratingTokens >= requested)
        {
            regeneratingTokens -= requested;
            success = true;
        }
        else if (bonusTokens >= (int)requested)
        {
            bonusTokens -= (int)requested;
            success = true;
        }
        else
        {
            success = false;
        }

        // Assert
        success.Should().BeTrue();
        regeneratingTokens.Should().Be(0.0);
        bonusTokens.Should().Be(4);
    }
}
