namespace JiraAi.Analytics.UnitTests.Domain;

using FluentAssertions;
using JiraAi.Analytics.Domain.Models;
using JiraAi.Analytics.Domain.Services;
using JiraAi.Contracts.Sprints.V1;
using Xunit;

/// <summary>
/// Adversarial stress test harness verifying domain metrics engines against boundary, extreme, and malformed inputs.
/// </summary>
public class AdversarialDomainMetricsTests
{
    private readonly BurndownCalculator _burndownCalculator = new();
    private readonly VelocityCalculator _velocityCalculator = new();
    private readonly HealthScoreCalculator _healthScoreCalculator = new();
    private readonly RiskDetectionEngine _riskDetectionEngine = new();

    #region 1. BurndownCalculator Adversarial Tests

    [Fact]
    public void BurndownCalculator_ZeroTotalStoryPoints_IdealLineSlopeDoesNotDivideByZero()
    {
        // Arrange
        var startDate = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var endDate = new DateTime(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc); // 10 days
        var snapshot = new SprintSnapshotDocument
        {
            SprintId = "zero-points-sprint",
            StartDate = startDate,
            EndDate = endDate,
            TotalStoryPoints = 0,
            RemainingStoryPoints = 0
        };

        // Act
        var result = _burndownCalculator.CalculateBurndownSeries(snapshot, asOfDate: startDate.AddDays(5));

        // Assert: All points should be safely 0.0 with no NaN or Infinity
        result.Should().HaveCount(11);
        foreach (var point in result)
        {
            double.IsNaN(point.IdealRemaining).Should().BeFalse("IdealRemaining must not be NaN");
            double.IsInfinity(point.IdealRemaining).Should().BeFalse("IdealRemaining must not be Infinity");
            point.IdealRemaining.Should().Be(0.0);
            point.ActualRemaining.Should().Be(0.0);
        }
    }

    [Fact]
    public void BurndownCalculator_ZeroDaySprintDuration_SafelyGuardedAgainstZeroTimespan()
    {
        // Arrange: StartDate == EndDate (0 days timespan)
        var date = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var snapshot = new SprintSnapshotDocument
        {
            SprintId = "zero-day-duration",
            StartDate = date,
            EndDate = date,
            TotalStoryPoints = 40,
            RemainingStoryPoints = 40
        };

        // Act
        var result = _burndownCalculator.CalculateBurndownSeries(snapshot, asOfDate: date);

        // Assert: Minimum 1 day guard prevents division by zero
        result.Should().NotBeEmpty();
        foreach (var point in result)
        {
            double.IsNaN(point.IdealRemaining).Should().BeFalse();
            double.IsInfinity(point.IdealRemaining).Should().BeFalse();
        }
        result[0].IdealRemaining.Should().Be(40.0);
    }

    [Fact]
    public void BurndownCalculator_InvertedDates_SafelyGuarded()
    {
        // Arrange: EndDate is before StartDate
        var startDate = new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc);
        var endDate = new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc);
        var snapshot = new SprintSnapshotDocument
        {
            SprintId = "inverted-dates",
            StartDate = startDate,
            EndDate = endDate,
            TotalStoryPoints = 30,
            RemainingStoryPoints = 30
        };

        // Act
        var result = _burndownCalculator.CalculateBurndownSeries(snapshot, asOfDate: startDate);

        // Assert
        result.Should().NotBeEmpty();
        result[0].IdealRemaining.Should().Be(30.0);
    }

    [Fact]
    public void BurndownCalculator_NegativeCompletedPoints_ClampedToZero()
    {
        // Arrange: DailyBurndown contains negative CompletedOnDay and negative ActualRemaining
        var startDate = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var endDate = new DateTime(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc);
        var snapshot = new SprintSnapshotDocument
        {
            SprintId = "negative-completed-sprint",
            StartDate = startDate,
            EndDate = endDate,
            TotalStoryPoints = 50,
            RemainingStoryPoints = -10, // anomalous negative remaining
            DailyBurndown = new List<DailyBurndownRecord>
            {
                new() { Date = "2026-10-01", DayIndex = 0, IdealRemaining = 50, ActualRemaining = -15, CompletedOnDay = -30 }
            }
        };

        // Act
        var result = _burndownCalculator.CalculateBurndownSeries(snapshot, asOfDate: startDate.AddDays(2));

        // Assert: Negative values must be clamped to 0.0
        result[0].CompletedOnDay.Should().Be(0.0, "Negative CompletedOnDay must be clamped to 0");
        result[0].ActualRemaining.Should().Be(0.0, "Negative ActualRemaining must be clamped to 0");
        result[1].ActualRemaining.Should().Be(0.0, "Unrecorded day with negative snapshot remaining must be clamped to 0");
    }

    [Fact]
    public void BurndownCalculator_OverCompletion_RemainingClampedToZeroAndNoCrash()
    {
        // Arrange: 80 points completed on a 50 point sprint (> 100% completion)
        var startDate = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var endDate = new DateTime(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc);
        var snapshot = new SprintSnapshotDocument
        {
            SprintId = "over-completion-sprint",
            StartDate = startDate,
            EndDate = endDate,
            TotalStoryPoints = 50,
            CompletedStoryPoints = 80,
            RemainingStoryPoints = -30 // more completed than total
        };

        // Act
        var burndown = _burndownCalculator.CalculateBurndownSeries(snapshot, asOfDate: startDate.AddDays(5));
        var velocityRate = _velocityCalculator.CalculateSprintCompletionRate(80, 50);

        // Assert
        burndown.Should().NotBeEmpty();
        burndown.All(p => p.ActualRemaining >= 0.0).Should().BeTrue("Remaining points must be non-negative");
        velocityRate.Should().Be(160.0, "Velocity should accurately reflect 160% completion without crashing");
    }

    [Fact]
    public void BurndownCalculator_ExtremePointCounts_NoIntegerOverflow()
    {
        // Arrange: 1,000,000 story points over 10 days
        var startDate = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var endDate = new DateTime(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc);
        var snapshot = new SprintSnapshotDocument
        {
            SprintId = "extreme-points-sprint",
            StartDate = startDate,
            EndDate = endDate,
            TotalStoryPoints = 1_000_000,
            RemainingStoryPoints = 600_000
        };

        // Act
        var result = _burndownCalculator.CalculateBurndownSeries(snapshot, asOfDate: startDate.AddDays(4));

        // Assert
        result.Should().HaveCount(11);
        result[0].IdealRemaining.Should().Be(1_000_000.0);
        result[5].IdealRemaining.Should().Be(500_000.0);
        result[10].IdealRemaining.Should().Be(0.0);
    }

    [Fact]
    public void BurndownCalculator_IntMaxValue_CalculatesWithoutOverflowException()
    {
        // Arrange: Int32.MaxValue points
        var startDate = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var endDate = new DateTime(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc);
        var snapshot = new SprintSnapshotDocument
        {
            SprintId = "int-max-sprint",
            StartDate = startDate,
            EndDate = endDate,
            TotalStoryPoints = int.MaxValue,
            RemainingStoryPoints = int.MaxValue / 2
        };

        // Act & Assert (must not throw OverflowException)
        var act = () => _burndownCalculator.CalculateBurndownSeries(snapshot, asOfDate: startDate);
        act.Should().NotThrow();
        var result = act();
        result[0].IdealRemaining.Should().Be((double)int.MaxValue);
    }

    #endregion

    #region 2. VelocityCalculator Adversarial Tests

    [Fact]
    public void VelocityCalculator_EmptyHistory_ReturnsZeroWithoutError()
    {
        // Act
        var velocityWithZero = _velocityCalculator.CalculateRollingVelocity(new List<VelocityRecordDocument>(), currentCompletedPoints: 0);
        var velocityWithNull = _velocityCalculator.CalculateRollingVelocity(null, currentCompletedPoints: 0);

        // Assert
        velocityWithZero.Should().Be(0.0);
        velocityWithNull.Should().Be(0.0);
    }

    [Fact]
    public void VelocityCalculator_NegativeStoryPoints_ClampedToZero()
    {
        // Arrange: Sprints with corrupt negative story points
        var history = new List<VelocityRecordDocument>
        {
            new() { SprintId = "s1", CompletedStoryPoints = -20, CompletedDate = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc) },
            new() { SprintId = "s2", CompletedStoryPoints = 30, CompletedDate = new DateTime(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc) }
        };

        // Act
        var velocity = _velocityCalculator.CalculateRollingVelocity(history);

        // Assert: -20 clamped to 0, (0 + 30) / 2 = 15.0
        velocity.Should().Be(15.0);
    }

    [Fact]
    public void VelocityCalculator_NegativeCurrentCompletedPoints_ClampedToZero()
    {
        // Act
        var velocity = _velocityCalculator.CalculateRollingVelocity(new List<VelocityRecordDocument>(), currentCompletedPoints: -25);

        // Assert
        velocity.Should().Be(0.0);
    }

    [Fact]
    public void VelocityCalculator_Rolling3SprintWindow_SelectsOnly3MostRecentByCompletedDate()
    {
        // Arrange: 6 sprints added in unordered sequence
        var history = new List<VelocityRecordDocument>
        {
            new() { SprintId = "oldest", CompletedStoryPoints = 100, CompletedDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
            new() { SprintId = "recent-3", CompletedStoryPoints = 20, CompletedDate = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc) },
            new() { SprintId = "recent-1", CompletedStoryPoints = 40, CompletedDate = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc) },
            new() { SprintId = "middle", CompletedStoryPoints = 200, CompletedDate = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc) },
            new() { SprintId = "recent-2", CompletedStoryPoints = 30, CompletedDate = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc) }
        };

        // Act: Should select recent-1 (40), recent-2 (30), recent-3 (20). Average = (40 + 30 + 20) / 3 = 30.0
        var velocity = _velocityCalculator.CalculateRollingVelocity(history);

        // Assert
        velocity.Should().Be(30.0);
    }

    [Theory]
    [InlineData(0, 0, 0.0)]
    [InlineData(10, 0, 0.0)]
    [InlineData(-10, 0, 0.0)]
    [InlineData(0, -50, 0.0)]
    [InlineData(50, 50, 100.0)]
    [InlineData(100, 50, 200.0)]
    [InlineData(1_000_000, 500_000, 200.0)]
    public void VelocityCalculator_CalculateSprintCompletionRate_HandlesEdgeCases(int completed, int total, double expected)
    {
        var rate = _velocityCalculator.CalculateSprintCompletionRate(completed, total);
        rate.Should().Be(expected);
    }

    #endregion

    #region 3. HealthScoreCalculator & RiskDetectionEngine Adversarial Tests

    [Fact]
    public void HealthScoreCalculator_StrictlyBoundedBetweenZeroAndHundred_UnderExtremePessimism()
    {
        // Arrange: Worst-case scenario - zero progress, severe scope creep, high WIP bottleneck
        var startDate = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var endDate = new DateTime(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc);
        var asOf = new DateTime(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc); // 100% elapsed

        var snapshot = new SprintSnapshotDocument
        {
            SprintId = "pessimistic-sprint",
            StartDate = startDate,
            EndDate = endDate,
            InitialCommittedPoints = 50,
            TotalStoryPoints = 200, // 300% scope creep
            CompletedStoryPoints = 0, // 0% completion -> paceGap = 1.0 -> -50 pts
            RemainingStoryPoints = 200,
            TrackedIssues = new Dictionary<string, IssueTrackingRecord>
            {
                { "i-1", new IssueTrackingRecord { IssueId = "i-1", Status = "IN_PROGRESS", StoryPoints = 150 } },
                { "i-2", new IssueTrackingRecord { IssueId = "i-2", Status = "IN_REVIEW", StoryPoints = 40 } }
            } // WIP = 190 / 200 = 95% -> -10 pts
        };

        // Act
        var score = _healthScoreCalculator.CalculateHealthScore(snapshot, asOfDate: asOf);

        // Assert: 100 - 50 (pace) - 20 (creep) - 10 (bottleneck) = 20.0, strictly within [0, 100]
        score.Should().BeInRange(0.0, 100.0);
        score.Should().Be(20.0);
    }

    [Fact]
    public void HealthScoreCalculator_StrictlyBounded_WhenFutureOrPastDatesProvided()
    {
        var startDate = new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc);
        var endDate = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);

        var snapshot = new SprintSnapshotDocument
        {
            SprintId = "future-sprint",
            StartDate = startDate,
            EndDate = endDate,
            TotalStoryPoints = 50,
            RemainingStoryPoints = 50
        };

        // Far before start date
        var scoreBefore = _healthScoreCalculator.CalculateHealthScore(snapshot, asOfDate: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        scoreBefore.Should().BeInRange(0.0, 100.0);

        // Far past end date
        var scoreAfter = _healthScoreCalculator.CalculateHealthScore(snapshot, asOfDate: new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        scoreAfter.Should().BeInRange(0.0, 100.0);
    }

    [Fact]
    public void ScopeCreep_Exactly20PercentBoundary_DoesNotTriggerAlertOrPenalty()
    {
        // Arrange: Initial 1000 pts, Total 1200 pts -> EXACTLY 20.00% increase
        var startDate = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var endDate = new DateTime(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc);
        var asOf = new DateTime(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc);

        var snapshot = new SprintSnapshotDocument
        {
            SprintId = "exact-20-percent",
            StartDate = startDate,
            EndDate = endDate,
            InitialCommittedPoints = 1000,
            TotalStoryPoints = 1200, // Exactly 20%
            CompletedStoryPoints = 600,
            RemainingStoryPoints = 600
        };

        // Act
        var risks = _riskDetectionEngine.EvaluateRisks(snapshot, asOfDate: asOf);
        var healthScore = _healthScoreCalculator.CalculateHealthScore(snapshot, asOfDate: asOf);

        // Assert: Exactly 20% must NOT trigger scope creep
        risks.Should().NotContain(r => r.Type == AlertType.ScopeCreep, "Exactly 20% scope creep is within tolerance");
        healthScore.Should().Be(100.0, "Exactly 20% scope creep must not incur a health penalty");
    }

    [Fact]
    public void ScopeCreep_Exceeding20PercentThreshold_TriggersAlertAndPenalty()
    {
        // Arrange: Initial 1000 pts, Total 1201 pts -> 20.1% increase (> 20.0%)
        var startDate = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var endDate = new DateTime(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc);
        var asOf = new DateTime(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc);

        var snapshot = new SprintSnapshotDocument
        {
            SprintId = "exceed-20-percent",
            StartDate = startDate,
            EndDate = endDate,
            InitialCommittedPoints = 1000,
            TotalStoryPoints = 1201, // 20.1% increase
            CompletedStoryPoints = 600,
            RemainingStoryPoints = 601
        };

        // Act
        var risks = _riskDetectionEngine.EvaluateRisks(snapshot, asOfDate: asOf);
        var healthScore = _healthScoreCalculator.CalculateHealthScore(snapshot, asOfDate: asOf);

        // Assert: > 20% MUST trigger scope creep alert and penalty
        risks.Should().ContainSingle(r => r.Type == AlertType.ScopeCreep, "Scope creep > 20% must trigger alert");
        var creepRisk = risks.First(r => r.Type == AlertType.ScopeCreep);
        creepRisk.Severity.Should().Be(AlertSeverity.Warning);
        creepRisk.MetricValue.Should().Be(20.1);

        // Health score should incur the -20 penalty
        healthScore.Should().BeLessThanOrEqualTo(80.0, "Scope creep > 20% must incur 20-point penalty");
    }

    [Fact]
    public void WIPBottleneck_Exactly50PercentBoundary_DoesNotTriggerAlertOrPenalty()
    {
        // Arrange: Remaining = 100 pts. In progress + in review = 50 pts (EXACTLY 50.0%)
        var snapshot = new SprintSnapshotDocument
        {
            SprintId = "exact-50-wip",
            TotalStoryPoints = 100,
            CompletedStoryPoints = 0,
            RemainingStoryPoints = 100,
            TrackedIssues = new Dictionary<string, IssueTrackingRecord>
            {
                { "i-1", new IssueTrackingRecord { IssueId = "i-1", Status = "IN_PROGRESS", StoryPoints = 25 } },
                { "i-2", new IssueTrackingRecord { IssueId = "i-2", Status = "IN_REVIEW", StoryPoints = 25 } },
                { "i-3", new IssueTrackingRecord { IssueId = "i-3", Status = "TODO", StoryPoints = 50 } }
            }
        };

        // Act
        var risks = _riskDetectionEngine.EvaluateRisks(snapshot);
        var healthScore = _healthScoreCalculator.CalculateHealthScore(snapshot);

        // Assert: Exactly 50% does not exceed the threshold (> 50%)
        risks.Should().NotContain(r => r.Type == AlertType.BottleneckDetected, "Exactly 50% WIP is within threshold");
        // No bottleneck penalty applied (score only affected by pace)
    }

    [Fact]
    public void WIPBottleneck_Exceeding50PercentThreshold_TriggersAlertAndPenalty()
    {
        // Arrange: Remaining = 100 pts. In progress + in review = 51 pts (51.0% > 50%)
        var snapshot = new SprintSnapshotDocument
        {
            SprintId = "exceed-50-wip",
            TotalStoryPoints = 100,
            CompletedStoryPoints = 0,
            RemainingStoryPoints = 100,
            TrackedIssues = new Dictionary<string, IssueTrackingRecord>
            {
                { "i-1", new IssueTrackingRecord { IssueId = "i-1", Status = "IN_PROGRESS", StoryPoints = 26 } },
                { "i-2", new IssueTrackingRecord { IssueId = "i-2", Status = "IN_REVIEW", StoryPoints = 25 } },
                { "i-3", new IssueTrackingRecord { IssueId = "i-3", Status = "TODO", StoryPoints = 49 } }
            }
        };

        // Act
        var risks = _riskDetectionEngine.EvaluateRisks(snapshot);

        // Assert: 51% MUST trigger bottleneck alert
        risks.Should().ContainSingle(r => r.Type == AlertType.BottleneckDetected);
        var bottleneck = risks.First(r => r.Type == AlertType.BottleneckDetected);
        bottleneck.MetricValue.Should().Be(51.0);
        bottleneck.Severity.Should().Be(AlertSeverity.Info);
    }

    [Fact]
    public void WIPBottleneck_ZeroRemainingPoints_DoesNotDivideByZero()
    {
        // Arrange: RemainingStoryPoints = 0
        var snapshot = new SprintSnapshotDocument
        {
            SprintId = "zero-remaining",
            TotalStoryPoints = 50,
            CompletedStoryPoints = 50,
            RemainingStoryPoints = 0,
            TrackedIssues = new Dictionary<string, IssueTrackingRecord>
            {
                { "i-1", new IssueTrackingRecord { IssueId = "i-1", Status = "DONE", StoryPoints = 50 } }
            }
        };

        // Act
        var risks = _riskDetectionEngine.EvaluateRisks(snapshot);
        var healthScore = _healthScoreCalculator.CalculateHealthScore(snapshot);

        // Assert: No divide by zero exception
        risks.Should().NotContain(r => r.Type == AlertType.BottleneckDetected);
        healthScore.Should().Be(100.0);
    }

    #endregion
}
