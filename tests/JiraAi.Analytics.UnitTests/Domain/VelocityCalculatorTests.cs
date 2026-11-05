namespace JiraAi.Analytics.UnitTests.Domain;

using FluentAssertions;
using JiraAi.Analytics.Domain.Models;
using JiraAi.Analytics.Domain.Services;
using Xunit;

public class VelocityCalculatorTests
{
    private readonly VelocityCalculator _sut = new();

    [Fact]
    public void CalculateRollingVelocity_T1_F19_03_ComputesAverageOverLast3Sprints()
    {
        // Arrange
        var history = new List<VelocityRecordDocument>
        {
            new() { SprintId = "s1", CompletedStoryPoints = 30, CompletedDate = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc) },
            new() { SprintId = "s2", CompletedStoryPoints = 40, CompletedDate = new DateTime(2026, 8, 15, 0, 0, 0, DateTimeKind.Utc) },
            new() { SprintId = "s3", CompletedStoryPoints = 50, CompletedDate = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc) },
            new() { SprintId = "s0", CompletedStoryPoints = 10, CompletedDate = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc) } // Should be excluded as 4th oldest
        };

        // Act: Most recent 3 are s3 (50), s2 (40), s1 (30). Average = 40.0
        var velocity = _sut.CalculateRollingVelocity(history);

        // Assert
        velocity.Should().Be(40.0);
    }

    [Fact]
    public void CalculateRollingVelocity_T2_F24_04_SingleCompletedSprint_ReturnsExactCompletedPoints()
    {
        // Arrange
        var history = new List<VelocityRecordDocument>
        {
            new() { SprintId = "s1", CompletedStoryPoints = 25, CompletedDate = DateTime.UtcNow }
        };

        // Act
        var velocity = _sut.CalculateRollingVelocity(history);

        // Assert
        velocity.Should().Be(25.0);
    }

    [Fact]
    public void CalculateRollingVelocity_EmptyHistory_ReturnsCurrentCompletedPoints()
    {
        var velocity = _sut.CalculateRollingVelocity(new List<VelocityRecordDocument>(), currentCompletedPoints: 18);
        velocity.Should().Be(18.0);
    }

    [Fact]
    public void CalculateRollingVelocity_NullHistory_ReturnsCurrentCompletedPoints()
    {
        var velocity = _sut.CalculateRollingVelocity(null, currentCompletedPoints: 12);
        velocity.Should().Be(12.0);
    }

    [Fact]
    public void CalculateSprintCompletionRate_T2_F19_01_ZeroTotalStoryPoints_ReturnsZeroWithoutDivisionByZero()
    {
        // Act
        var rate = _sut.CalculateSprintCompletionRate(completedPoints: 0, totalPoints: 0);

        // Assert
        rate.Should().Be(0.0);
    }

    [Fact]
    public void CalculateSprintCompletionRate_T2_F19_04_OverAchievement_ComputesGreaterThan100PercentSafely()
    {
        // Act: 60 completed out of 50 committed
        var rate = _sut.CalculateSprintCompletionRate(completedPoints: 60, totalPoints: 50);

        // Assert
        rate.Should().Be(120.0);
    }

    [Fact]
    public void CalculateSprintCompletionRate_StandardPace_ReturnsCorrectPercentage()
    {
        var rate = _sut.CalculateSprintCompletionRate(completedPoints: 25, totalPoints: 100);
        rate.Should().Be(25.0);
    }
}
