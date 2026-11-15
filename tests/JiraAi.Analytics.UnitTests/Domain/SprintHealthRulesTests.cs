namespace JiraAi.Analytics.UnitTests.Domain;

using FluentAssertions;
using JiraAi.Analytics.Domain.Models;
using JiraAi.Analytics.Domain.Services;
using Xunit;

public class SprintHealthRulesTests
{
    private static readonly DateTime Today = new(2026, 11, 15, 0, 0, 0, DateTimeKind.Utc);

    private static SprintSnapshotDocument Running(int totalPoints, string status = SnapshotStatus.Active) => new()
    {
        Status = status,
        StartDate = Today.AddDays(-3),
        EndDate = Today.AddDays(7),
        TotalStoryPoints = totalPoints
    };

    [Theory]
    [InlineData(100, SprintHealthStatus.Healthy)]
    [InlineData(75, SprintHealthStatus.Healthy)]
    [InlineData(74.99, SprintHealthStatus.AtRisk)]
    [InlineData(50, SprintHealthStatus.AtRisk)]
    [InlineData(49.99, SprintHealthStatus.Critical)]
    [InlineData(0, SprintHealthStatus.Critical)]
    public void Classify_StartedSprintWithScope_UsesTheScoreThresholds(double score, SprintHealthStatus expected)
    {
        SprintHealthRules.Classify(Running(20), score, Today).Should().Be(expected);
    }

    [Fact]
    public void Classify_PlannedSprint_IsNotStartedWhateverTheScore()
    {
        SprintHealthRules.Classify(Running(20, SnapshotStatus.Planned), 10, Today).Should().Be(SprintHealthStatus.NotStarted);
    }

    [Fact]
    public void Classify_SprintBeforeItsStartDate_IsNotStarted()
    {
        var snapshot = Running(20);
        snapshot.StartDate = Today.AddDays(1);

        SprintHealthRules.Classify(snapshot, 10, Today).Should().Be(SprintHealthStatus.NotStarted);
        SprintHealthRules.Classify(snapshot, 10, Today.AddDays(1)).Should().Be(SprintHealthStatus.Critical,
            "on its start day the sprint has started");
    }

    [Fact]
    public void Classify_NotStartedWinsOverNoScope()
    {
        SprintHealthRules.Classify(Running(0, SnapshotStatus.Planned), 100, Today).Should().Be(SprintHealthStatus.NotStarted);
    }

    [Fact]
    public void Classify_StartedSprintWithoutPoints_IsNoScope()
    {
        // The score calculator gives 100 to a sprint without points; that is not "healthy".
        SprintHealthRules.Classify(Running(0), 100, Today).Should().Be(SprintHealthStatus.NoScope);
        SprintHealthRules.Classify(Running(0, SnapshotStatus.Completed), 100, Today).Should().Be(SprintHealthStatus.NoScope);
    }

    [Fact]
    public void Classify_WithoutDate_UsesToday()
    {
        var snapshot = Running(20);
        snapshot.StartDate = DateTime.UtcNow.AddDays(-1);
        SprintHealthRules.Classify(snapshot, 80).Should().Be(SprintHealthStatus.Healthy);
        FluentActions.Invoking(() => SprintHealthRules.Classify(null!, 80)).Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData(SprintHealthStatus.Healthy, "HEALTHY")]
    [InlineData(SprintHealthStatus.AtRisk, "AT_RISK")]
    [InlineData(SprintHealthStatus.Critical, "CRITICAL")]
    [InlineData(SprintHealthStatus.NotStarted, "NOT_STARTED")]
    [InlineData(SprintHealthStatus.NoScope, "NO_SCOPE")]
    public void ToWireValue_MatchesTheContractEnum(SprintHealthStatus status, string expected)
    {
        SprintHealthRules.ToWireValue(status).Should().Be(expected);
    }

    [Fact]
    public void ToWireValue_UnknownValue_Throws()
    {
        FluentActions.Invoking(() => SprintHealthRules.ToWireValue((SprintHealthStatus)99))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void RollingSampleSize_CountsTheClosedSprintsAveraged()
    {
        VelocityCalculator.RollingSampleSize(null).Should().Be(0);
        VelocityCalculator.RollingSampleSize(new List<VelocityRecordDocument>()).Should().Be(0);
        VelocityCalculator.RollingSampleSize(Enumerable.Range(0, 2).Select(_ => new VelocityRecordDocument())).Should().Be(2);
        VelocityCalculator.RollingSampleSize(Enumerable.Range(0, 5).Select(_ => new VelocityRecordDocument()))
            .Should().Be(VelocityCalculator.RollingWindowSize, "only the last 3 closed sprints are averaged");
    }
}
