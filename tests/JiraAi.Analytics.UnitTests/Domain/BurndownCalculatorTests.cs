namespace JiraAi.Analytics.UnitTests.Domain;

using FluentAssertions;
using JiraAi.Analytics.Domain.Models;
using JiraAi.Analytics.Domain.Services;
using Xunit;

public class BurndownCalculatorTests
{
    private readonly BurndownCalculator _sut = new();

    [Fact]
    public void CalculateBurndownSeries_T1_F19_01_ComputesIdealBurndownTrajectory()
    {
        // Arrange
        var startDate = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var endDate = new DateTime(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc); // 10 days
        var snapshot = new SprintSnapshotDocument
        {
            SprintId = "sprint-1",
            StartDate = startDate,
            EndDate = endDate,
            TotalStoryPoints = 100,
            RemainingStoryPoints = 100
        };

        // Act
        var result = _sut.CalculateBurndownSeries(snapshot, asOfDate: startDate);

        // Assert
        result.Should().HaveCount(11); // Day 0 to Day 10 inclusive
        result[0].DayIndex.Should().Be(0);
        result[0].IdealRemaining.Should().Be(100.0);
        result[5].DayIndex.Should().Be(5);
        result[5].IdealRemaining.Should().Be(50.0);
        result[10].DayIndex.Should().Be(10);
        result[10].IdealRemaining.Should().Be(0.0);
    }

    [Fact]
    public void CalculateBurndownSeries_T1_F19_02_ComputesActualRemainingPointsPerDay()
    {
        // Arrange
        var startDate = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var endDate = new DateTime(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc);
        var snapshot = new SprintSnapshotDocument
        {
            SprintId = "sprint-2",
            StartDate = startDate,
            EndDate = endDate,
            TotalStoryPoints = 50,
            RemainingStoryPoints = 30,
            DailyBurndown = new List<DailyBurndownRecord>
            {
                new() { Date = "2026-10-01", DayIndex = 0, IdealRemaining = 50, ActualRemaining = 50, CompletedOnDay = 0 },
                new() { Date = "2026-10-02", DayIndex = 1, IdealRemaining = 40, ActualRemaining = 45, CompletedOnDay = 5 },
                new() { Date = "2026-10-03", DayIndex = 2, IdealRemaining = 30, ActualRemaining = 35, CompletedOnDay = 15 }
            }
        };

        // Act
        var result = _sut.CalculateBurndownSeries(snapshot, asOfDate: startDate.AddDays(2));

        // Assert
        result[0].ActualRemaining.Should().Be(50);
        result[1].ActualRemaining.Should().Be(45);
        result[1].CompletedOnDay.Should().Be(5);
        result[2].ActualRemaining.Should().Be(35);
        result[2].CompletedOnDay.Should().Be(15);
    }

    [Fact]
    public void CalculateBurndownSeries_T2_F19_02_ZeroOrNegativeDuration_HandledWithMinimumOneDay()
    {
        // Arrange: StartDate == EndDate
        var date = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var snapshot = new SprintSnapshotDocument
        {
            SprintId = "sprint-zero-days",
            StartDate = date,
            EndDate = date, // 0 days
            TotalStoryPoints = 20,
            RemainingStoryPoints = 20
        };

        // Act
        var result = _sut.CalculateBurndownSeries(snapshot, asOfDate: date);

        // Assert: Guarantees no division by zero exception
        result.Should().NotBeEmpty();
        result[0].IdealRemaining.Should().Be(20.0);
    }

    [Fact]
    public void CalculateBurndownSeries_T2_F19_03_NegativeCompletedPoints_ClampedToZero()
    {
        // Arrange
        var date = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var snapshot = new SprintSnapshotDocument
        {
            SprintId = "sprint-negative",
            StartDate = date,
            EndDate = date.AddDays(5),
            TotalStoryPoints = 30,
            RemainingStoryPoints = 30,
            DailyBurndown = new List<DailyBurndownRecord>
            {
                new() { Date = "2026-10-01", DayIndex = 0, IdealRemaining = 30, ActualRemaining = 30, CompletedOnDay = -10 }
            }
        };

        // Act
        var result = _sut.CalculateBurndownSeries(snapshot, asOfDate: date);

        // Assert
        result[0].CompletedOnDay.Should().Be(0.0);
    }

    [Fact]
    public void CalculateBurndownSeries_T2_F19_05_ExtremelyLargePointCount_HandledWithoutOverflow()
    {
        // Arrange: 1,000,000 points
        var date = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var snapshot = new SprintSnapshotDocument
        {
            SprintId = "sprint-million",
            StartDate = date,
            EndDate = date.AddDays(10),
            TotalStoryPoints = 1_000_000,
            RemainingStoryPoints = 500_000
        };

        // Act
        var result = _sut.CalculateBurndownSeries(snapshot, asOfDate: date.AddDays(5));

        // Assert
        result[0].IdealRemaining.Should().Be(1_000_000.0);
        result[5].IdealRemaining.Should().Be(500_000.0);
        result[10].IdealRemaining.Should().Be(0.0);
    }

    [Fact]
    public void CalculateBurndownSeries_NullSnapshot_ThrowsArgumentNullException()
    {
        var act = () => _sut.CalculateBurndownSeries(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
