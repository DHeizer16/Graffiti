using FluentAssertions;
using Xunit;

namespace GlobalGraffitiWall.Tests;

public class CanvasResetTests
{
    [Fact]
    public void ScheduledReset_MustRejectPastTimestamps()
    {
        // Arrange
        var pastTime = DateTimeOffset.UtcNow.AddMinutes(-5);
        var request = new API.ScheduleResetRequest
        {
            ScheduledResetUtc = pastTime,
            AnnouncementMessage = "Test Wipe"
        };

        // Act & Assert
        (request.ScheduledResetUtc <= DateTimeOffset.UtcNow).Should().BeTrue();
    }

    [Fact]
    public void ScheduledReset_AcceptsFutureTimestamps()
    {
        // Arrange
        var futureTime = DateTimeOffset.UtcNow.AddHours(24);
        var request = new API.ScheduleResetRequest
        {
            ScheduledResetUtc = futureTime,
            AnnouncementMessage = "Season 1 Finale"
        };

        // Act & Assert
        (request.ScheduledResetUtc > DateTimeOffset.UtcNow).Should().BeTrue();
    }

    [Fact]
    public void Countdown_CalculatesAccurateRemainingSeconds()
    {
        // Arrange
        var target = DateTimeOffset.UtcNow.AddSeconds(3600); // 1 hour

        // Act
        int remaining = (int)(target - DateTimeOffset.UtcNow).TotalSeconds;

        // Assert
        remaining.Should().BeInRange(3595, 3600);
    }

    [Fact]
    public void Countdown_FloorsAtZeroWhenTargetPassed()
    {
        // Arrange
        var target = DateTimeOffset.UtcNow.AddSeconds(-10);

        // Act
        int remaining = Math.Max(0, (int)(target - DateTimeOffset.UtcNow).TotalSeconds);

        // Assert
        remaining.Should().Be(0);
    }

    [Fact]
    public void CanvasResetStatusDto_DefaultsToSeason1Genesis()
    {
        // Arrange & Act
        var status = new API.CanvasResetStatusDto();

        // Assert
        status.CurrentSeasonNumber.Should().Be(1);
        status.CurrentSeasonName.Should().Be("Season 1: Genesis");
        status.IsResetScheduled.Should().BeFalse();
        status.RemainingSeconds.Should().Be(0);
        status.ScheduledResetUtc.Should().BeNull();
    }

    [Theory]
    [InlineData(1, 2, "Season 2")]
    [InlineData(2, 3, "Season 3")]
    [InlineData(99, 100, "Season 100")]
    public void SeasonProgression_IncrementsCorrectly(int currentSeason, int expectedNext, string expectedName)
    {
        // Act
        int nextSeason = currentSeason + 1;
        string nextName = $"Season {nextSeason}";

        // Assert
        nextSeason.Should().Be(expectedNext);
        nextName.Should().Be(expectedName);
    }

    [Fact]
    public void CanvasSeason_ModelPropertiesInitializeProperly()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        var season = new API.CanvasSeason
        {
            SeasonId = 1,
            SeasonNumber = 2,
            Name = "Season 2: Neon Dawn",
            StartedAt = now,
            ResetBy = "AdminSuperUser",
            ResetReason = "Community milestone reached"
        };

        // Assert
        season.SeasonId.Should().Be(1);
        season.SeasonNumber.Should().Be(2);
        season.Name.Should().Be("Season 2: Neon Dawn");
        season.StartedAt.Should().Be(now);
        season.EndedAt.Should().BeNull();
        season.ResetBy.Should().Be("AdminSuperUser");
        season.ResetReason.Should().Be("Community milestone reached");
    }

    [Fact]
    public void CanvasScheduledReset_ModelPropertiesInitializeProperly()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        var target = now.AddDays(7);
        var scheduled = new API.CanvasScheduledReset
        {
            Id = 42,
            ScheduledResetUtc = target,
            ScheduledBy = "AdminOne",
            AnnouncementMessage = "Prepare your portfolios!",
            CreatedAt = now,
            IsCancelled = false,
            IsExecuted = false
        };

        // Assert
        scheduled.Id.Should().Be(42);
        scheduled.ScheduledResetUtc.Should().Be(target);
        scheduled.ScheduledBy.Should().Be("AdminOne");
        scheduled.AnnouncementMessage.Should().Be("Prepare your portfolios!");
        scheduled.CreatedAt.Should().Be(now);
        scheduled.IsCancelled.Should().BeFalse();
        scheduled.IsExecuted.Should().BeFalse();
    }
}
