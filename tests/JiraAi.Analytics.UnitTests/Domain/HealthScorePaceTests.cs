namespace JiraAi.Analytics.UnitTests.Domain;

using FluentAssertions;
using JiraAi.Analytics.Domain.Models;
using JiraAi.Analytics.Domain.Services;
using Xunit;

/// <summary>Only being behind the ideal line lowers the health score; being ahead never does.</summary>
public class HealthScorePaceTests
{
    private static readonly DateTime Start = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End = new(2026, 11, 11, 0, 0, 0, DateTimeKind.Utc); // 10 days

    private readonly HealthScoreCalculator _sut = new();

    /// <summary>A 20 SP sprint (no scope change, no WIP) on the given day with the given points done.</summary>
    private double ScoreOnDay(int day, int completed) => _sut.CalculateHealthScore(new SprintSnapshotDocument
    {
        Status = SnapshotStatus.Active,
        StartDate = Start,
        EndDate = End,
        InitialCommittedPoints = 20,
        TotalStoryPoints = 20,
        CompletedStoryPoints = completed,
        RemainingStoryPoints = 20 - completed,
    }, asOfDate: Start.AddDays(day));

    [Theory]
    [InlineData(0, 18)] // day 0, 90% done
    [InlineData(2, 12)] // 20% of the time, 60% done
    [InlineData(5, 15)] // half the time, 75% done
    public void AheadOfTheIdealLine_KeepsFullHealth(int day, int completed)
    {
        ScoreOnDay(day, completed).Should().Be(100.0);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(5, 10)]
    public void OnTheIdealLine_KeepsFullHealth(int day, int completed)
    {
        ScoreOnDay(day, completed).Should().Be(100.0);
    }

    [Theory]
    [InlineData(5, 5, 87.5)]  // 50% time, 25% done -> 25% behind -> -12.5
    [InlineData(8, 4, 70.0)]  // 80% time, 20% done -> 60% behind -> -30
    [InlineData(10, 0, 50.0)] // all the time, nothing done -> -50
    public void BehindTheIdealLine_LosesHealthProportionally(int day, int completed, double expected)
    {
        ScoreOnDay(day, completed).Should().Be(expected);
    }

    [Fact]
    public void BeingFurtherAhead_NeverScoresLowerThanBeingOnTrack()
    {
        ScoreOnDay(3, 20 * 3 / 10).Should().Be(100.0);
        ScoreOnDay(3, 19).Should().BeGreaterThanOrEqualTo(ScoreOnDay(3, 6));
    }

    [Fact]
    public void AheadSprint_IsClassifiedHealthy()
    {
        // Reported case: 16 SP, only 2 SP left on day 0 of a two-week sprint.
        var asOf = new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
        var snapshot = new SprintSnapshotDocument
        {
            Status = SnapshotStatus.Active,
            StartDate = new DateTime(2026, 11, 15, 3, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2026, 11, 29, 2, 59, 59, DateTimeKind.Utc),
            InitialCommittedPoints = 16,
            TotalStoryPoints = 16,
            CompletedStoryPoints = 14,
            RemainingStoryPoints = 2,
        };

        var score = _sut.CalculateHealthScore(snapshot, asOf);

        score.Should().Be(100.0);
        SprintHealthRules.Classify(snapshot, score, asOf).Should().Be(SprintHealthStatus.Healthy);
    }
}
