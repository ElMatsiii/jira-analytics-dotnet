namespace JiraAi.Analytics.UnitTests.Domain;

using FluentAssertions;
using JiraAi.Analytics.Domain.Models;
using JiraAi.Analytics.Domain.Services;
using Xunit;

public class HealthScoreCalculatorTests
{
    private readonly HealthScoreCalculator _sut = new();

    [Fact]
    public void CalculateHealthScore_T1_F19_04_Returns100WhenCompleted()
    {
        var snapshot = new SprintSnapshotDocument
        {
            SprintId = "sprint-completed",
            Status = "COMPLETED",
            TotalStoryPoints = 40,
            CompletedStoryPoints = 40,
            RemainingStoryPoints = 0
        };

        var score = _sut.CalculateHealthScore(snapshot);
        score.Should().Be(100.0);
    }

    [Fact]
    public void CalculateHealthScore_ZeroTotalStoryPoints_Returns100()
    {
        var snapshot = new SprintSnapshotDocument
        {
            SprintId = "sprint-empty",
            Status = "ACTIVE",
            TotalStoryPoints = 0,
            CompletedStoryPoints = 0,
            RemainingStoryPoints = 0
        };

        var score = _sut.CalculateHealthScore(snapshot);
        score.Should().Be(100.0);
    }

    [Fact]
    public void CalculateHealthScore_OnTrackPace_ComputesHighHealthScore()
    {
        // 50% time elapsed (day 5 of 10), 50% completed (25 of 50 pts)
        var startDate = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var endDate = new DateTime(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc);
        var asOf = new DateTime(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc);

        var snapshot = new SprintSnapshotDocument
        {
            SprintId = "sprint-ontrack",
            StartDate = startDate,
            EndDate = endDate,
            InitialCommittedPoints = 50,
            TotalStoryPoints = 50,
            CompletedStoryPoints = 25,
            RemainingStoryPoints = 25
        };

        var score = _sut.CalculateHealthScore(snapshot, asOfDate: asOf);
        score.Should().Be(100.0);
    }

    [Fact]
    public void CalculateHealthScore_ScopeCreep_PenalizedBy20Points()
    {
        // Initial = 50, Total = 65 (> 20% creep)
        var startDate = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var endDate = new DateTime(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc);
        var asOf = new DateTime(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc);

        var snapshot = new SprintSnapshotDocument
        {
            SprintId = "sprint-scopecreep",
            StartDate = startDate,
            EndDate = endDate,
            InitialCommittedPoints = 50,
            TotalStoryPoints = 65, // 30% increase
            CompletedStoryPoints = 32,
            RemainingStoryPoints = 33
        };

        var score = _sut.CalculateHealthScore(snapshot, asOfDate: asOf);
        // Score should be reduced due to scope creep penalty
        score.Should().BeLessThanOrEqualTo(80.0);
    }

    [Fact]
    public void CalculateHealthScore_BottleneckPenalty_AppliedWhenHighWIP()
    {
        var startDate = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var endDate = new DateTime(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc);
        var asOf = new DateTime(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc);

        var snapshot = new SprintSnapshotDocument
        {
            SprintId = "sprint-bottleneck",
            StartDate = startDate,
            EndDate = endDate,
            InitialCommittedPoints = 40,
            TotalStoryPoints = 40,
            CompletedStoryPoints = 20,
            RemainingStoryPoints = 20,
            TrackedIssues = new Dictionary<string, IssueTrackingRecord>
            {
                { "i-1", new IssueTrackingRecord { IssueId = "i-1", Status = "IN_PROGRESS", StoryPoints = 8 } },
                { "i-2", new IssueTrackingRecord { IssueId = "i-2", Status = "IN_REVIEW", StoryPoints = 8 } },
                { "i-3", new IssueTrackingRecord { IssueId = "i-3", Status = "TODO", StoryPoints = 4 } }
            } // 16 of 20 remaining points (80%) are in WIP > 50% threshold
        };

        var score = _sut.CalculateHealthScore(snapshot, asOfDate: asOf);
        score.Should().BeLessThanOrEqualTo(90.0);
    }

    [Fact]
    public void CalculateHealthScore_ClampedBetweenZeroAndHundred()
    {
        // Massive lag: 90% time elapsed, 0% completed, scope creep
        var startDate = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var endDate = new DateTime(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc);
        var asOf = new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc);

        var snapshot = new SprintSnapshotDocument
        {
            SprintId = "sprint-disaster",
            StartDate = startDate,
            EndDate = endDate,
            InitialCommittedPoints = 30,
            TotalStoryPoints = 60,
            CompletedStoryPoints = 0,
            RemainingStoryPoints = 60
        };

        var score = _sut.CalculateHealthScore(snapshot, asOfDate: asOf);
        score.Should().BeInRange(0.0, 100.0);
    }

    [Fact]
    public void CalculateHealthScore_NullSnapshot_ThrowsArgumentNullException()
    {
        var act = () => _sut.CalculateHealthScore(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
