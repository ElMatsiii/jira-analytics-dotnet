namespace JiraAi.Analytics.UnitTests.Domain;

using FluentAssertions;
using JiraAi.Analytics.Domain.Models;
using JiraAi.Analytics.Domain.Services;
using JiraAi.Contracts.Sprints.V1;
using Xunit;

public class RiskDetectionEngineTests
{
    private readonly RiskDetectionEngine _sut = new();

    [Fact]
    public void EvaluateRisks_T1_F19_05_ScopeCreepExceeding20Percent_DetectsRisk()
    {
        // Arrange: Initial 40 pts, Total 55 pts -> 37.5% increase > 20%
        var snapshot = new SprintSnapshotDocument
        {
            SprintId = "sprint-creep",
            InitialCommittedPoints = 40,
            TotalStoryPoints = 55,
            CompletedStoryPoints = 10,
            RemainingStoryPoints = 45
        };

        // Act
        var risks = _sut.EvaluateRisks(snapshot);

        // Assert
        risks.Should().ContainSingle(r => r.Type == AlertType.ScopeCreep);
        var risk = risks.First(r => r.Type == AlertType.ScopeCreep);
        risk.Severity.Should().Be(AlertSeverity.Warning);
        risk.MetricValue.Should().Be(37.5);
        risk.Message.Should().Contain("Scope creep of 37.5%");
    }

    [Fact]
    public void EvaluateRisks_ScopeCreepUnder20Percent_DoesNotTriggerAlert()
    {
        // Arrange: Initial 50 pts, Total 55 pts -> 10% increase (< 20%)
        var snapshot = new SprintSnapshotDocument
        {
            SprintId = "sprint-ok",
            InitialCommittedPoints = 50,
            TotalStoryPoints = 55,
            CompletedStoryPoints = 15,
            RemainingStoryPoints = 40
        };

        // Act
        var risks = _sut.EvaluateRisks(snapshot);

        // Assert
        risks.Should().NotContain(r => r.Type == AlertType.ScopeCreep);
    }

    [Fact]
    public void EvaluateRisks_T1_F23_02_VelocityDeficitDetected_WhenBehindSchedule()
    {
        // Arrange: 60% time elapsed, ideal remaining = 40 pts, actual remaining = 70 pts (> 40 * 1.30 = 52)
        var startDate = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var endDate = new DateTime(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc); // 10 days
        var asOf = new DateTime(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc); // day 6 (60%)

        var snapshot = new SprintSnapshotDocument
        {
            SprintId = "sprint-deficit",
            StartDate = startDate,
            EndDate = endDate,
            InitialCommittedPoints = 100,
            TotalStoryPoints = 100,
            CompletedStoryPoints = 25,
            RemainingStoryPoints = 75 // 75 > 40 * 1.30 = 52
        };

        // Act
        var risks = _sut.EvaluateRisks(snapshot, asOfDate: asOf);

        // Assert
        risks.Should().Contain(r => r.Type == AlertType.VelocityDeficit);
        var risk = risks.First(r => r.Type == AlertType.VelocityDeficit);
        risk.Severity.Should().Be(AlertSeverity.Warning);
        risk.Message.Should().Contain("Velocity deficit detected");
    }

    [Fact]
    public void EvaluateRisks_BottleneckDetected_WhenHighWIP()
    {
        var snapshot = new SprintSnapshotDocument
        {
            SprintId = "sprint-wip",
            TotalStoryPoints = 30,
            RemainingStoryPoints = 20,
            TrackedIssues = new Dictionary<string, IssueTrackingRecord>
            {
                { "i-1", new IssueTrackingRecord { IssueId = "i-1", Status = "IN_PROGRESS", StoryPoints = 8 } },
                { "i-2", new IssueTrackingRecord { IssueId = "i-2", Status = "IN_REVIEW", StoryPoints = 8 } },
                { "i-3", new IssueTrackingRecord { IssueId = "i-3", Status = "TODO", StoryPoints = 4 } }
            }
        };

        var risks = _sut.EvaluateRisks(snapshot);
        risks.Should().Contain(r => r.Type == AlertType.BottleneckDetected);
    }

    [Fact]
    public void GenerateDiagnosticsAndRecommendations_ProducesOutput()
    {
        var snapshot = new SprintSnapshotDocument
        {
            SprintId = "sprint-clean",
            TotalStoryPoints = 30,
            CompletedStoryPoints = 15,
            RemainingStoryPoints = 15
        };

        var (diagnostics, recommendations) = _sut.GenerateDiagnosticsAndRecommendations(snapshot);
        diagnostics.Should().NotBeEmpty();
        recommendations.Should().NotBeEmpty();
    }
}
