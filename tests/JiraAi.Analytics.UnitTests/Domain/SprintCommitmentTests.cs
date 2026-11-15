namespace JiraAi.Analytics.UnitTests.Domain;

using FluentAssertions;
using JiraAi.Analytics.Contracts.Dtos;
using JiraAi.Analytics.Domain.Models;
using JiraAi.Analytics.Domain.Services;
using JiraAi.Analytics.Infrastructure.Grpc;
using JiraAi.Analytics.Infrastructure.Messaging;
using JiraAi.Analytics.Infrastructure.Persistence;
using JiraAi.Analytics.UnitTests.TestDoubles;
using JiraAi.Contracts.Sprints.V1;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

/// <summary>The scope-creep base is the sprint's points when it started (fixed by MS2), not its first issue.</summary>
public class SprintCommitmentTests
{
    private static readonly DateTime Today = DateTime.UtcNow.Date;

    private static SprintInfo Info(SprintLifecycleStatus status, int committed) =>
        new("sprint-1", "project-1", "Sprint 1", Today.AddDays(-1), Today.AddDays(13), status, committed);

    [Fact]
    public void ApplyCommitment_PlannedSprint_HasNoCommitmentYet()
    {
        var snapshot = new SprintSnapshotDocument { Status = SnapshotStatus.Planned, InitialCommittedPoints = 3, TotalStoryPoints = 16 };

        SprintSnapshotRules.ApplyCommitment(snapshot, Info(SprintLifecycleStatus.Planned, 0)).Should().BeTrue();

        snapshot.InitialCommittedPoints.Should().Be(0, "issues added while planning are not scope creep");
    }

    [Fact]
    public void ApplyCommitment_ActiveSprint_UsesThePointsFixedByMs2AtStart()
    {
        // Reported case: the first issue (3 SP) had been taken as the commitment of a 16 SP sprint.
        var snapshot = new SprintSnapshotDocument { Status = SnapshotStatus.Active, InitialCommittedPoints = 3, TotalStoryPoints = 16 };

        SprintSnapshotRules.ApplyCommitment(snapshot, Info(SprintLifecycleStatus.Active, 16)).Should().BeTrue();

        snapshot.InitialCommittedPoints.Should().Be(16);
        SprintSnapshotRules.ApplyCommitment(snapshot, Info(SprintLifecycleStatus.Active, 16)).Should().BeFalse("nothing changed");
    }

    [Theory]
    [InlineData(SprintLifecycleStatus.Active, 0)]    // started without points
    [InlineData(SprintLifecycleStatus.Unknown, 20)]  // status MS3 does not understand
    [InlineData(SprintLifecycleStatus.Cancelled, 20)]
    public void ApplyCommitment_WithoutAUsableCommitment_KeepsTheCurrentOne(SprintLifecycleStatus status, int committed)
    {
        var snapshot = new SprintSnapshotDocument { Status = SnapshotStatus.Active, InitialCommittedPoints = 7 };

        SprintSnapshotRules.ApplyCommitment(snapshot, Info(status, committed)).Should().BeFalse();

        snapshot.InitialCommittedPoints.Should().Be(7);
    }

    [Fact]
    public void ApplyCommitment_ClosedSnapshot_KeepsItsCommitment()
    {
        // On completion MS2 overwrites total_story_points with the final total, which is not the commitment.
        var snapshot = new SprintSnapshotDocument { Status = SnapshotStatus.Completed, InitialCommittedPoints = 10 };

        SprintSnapshotRules.ApplyCommitment(snapshot, Info(SprintLifecycleStatus.Active, 25)).Should().BeFalse();

        snapshot.InitialCommittedPoints.Should().Be(10);
    }

    [Fact]
    public void ApplyCommitment_RequiresBothArguments()
    {
        FluentActions.Invoking(() => SprintSnapshotRules.ApplyCommitment(null!, Info(SprintLifecycleStatus.Active, 1)))
            .Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => SprintSnapshotRules.ApplyCommitment(new SprintSnapshotDocument(), null!))
            .Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ToSprintInfo_ReadsTheCommittedPointsFromMs2()
    {
        var info = SprintDirectoryClient.ToSprintInfo(new Sprint { Id = "s1", Status = SprintStatus.Active, TotalStoryPoints = 16 });
        info.CommittedStoryPoints.Should().Be(16);

        SprintDirectoryClient.ToSprintInfo(new Sprint { Id = "s1", TotalStoryPoints = -4 }).CommittedStoryPoints.Should().Be(0);
    }
}

/// <summary>End to end through the event and query processors with MS2 faked.</summary>
public class SprintCommitmentFlowTests
{
    private static readonly DateTime Today = DateTime.UtcNow.Date;
    private readonly InMemorySnapshotRepository _snapshots = new();
    private readonly InMemoryVelocityRepository _velocity = new();
    private readonly FakeSprintDirectory _directory = new();
    private readonly IAlertClientService _alerts = Substitute.For<IAlertClientService>();

    public SprintCommitmentFlowTests()
    {
        _alerts.TriggerSprintAlertAsync(default!, default, default, default!, default!, default)
            .ReturnsForAnyArgs(Task.FromResult(AlertResult.Succeeded("a")));
    }

    private IssuesEventProcessor Events() => new(
        _snapshots, new RiskDetectionEngine(), _alerts, TestSprintSync.Create(_snapshots, _velocity, _directory),
        NullLogger<IssuesEventProcessor>.Instance);

    private AnalyticsQueryProcessor Queries() => new(
        _snapshots, _velocity, new BurndownCalculator(), new VelocityCalculator(), new HealthScoreCalculator(),
        new RiskDetectionEngine(), Substitute.For<IKafkaProducerWrapper>(), Options.Create(new KafkaOptions()),
        NullLogger<AnalyticsQueryProcessor>.Instance, TestSprintSync.Create(_snapshots, _velocity, _directory));

    private void Ms2Says(SprintLifecycleStatus status, int committed) => _directory.With(new SprintInfo(
        "sprint-1", "project-1", "Sprint 1", Today.AddDays(-1), Today.AddDays(13), status, committed));

    private Task Assign(string issueId, int points) => Events().ProcessEnvelopeAsync(new IssueEventEnvelope
    {
        EventId = Guid.NewGuid().ToString(),
        EventType = "ISSUE_SPRINT_ASSIGNED",
        Timestamp = DateTime.UtcNow.ToString("o"),
        Payload = new IssueEventPayload { Id = issueId, Key = issueId, ProjectId = "project-1", SprintId = "sprint-1", Status = "TODO", StoryPoints = points },
    });

    private Task ReceivedScopeCreepAlerts(int count) => _alerts.Received(count).TriggerSprintAlertAsync(
        "sprint-1", RiskType.ScopeCreep, Arg.Any<RiskSeverity>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

    [Fact]
    public async Task PlanningIsNotScopeCreep_GrowingAfterTheStartIs()
    {
        // Planning: 3 + 5 + 8 = 16 SP are added before the sprint starts.
        Ms2Says(SprintLifecycleStatus.Planned, 0);
        await Assign("i-1", 3);
        await Assign("i-2", 5);
        await Assign("i-3", 8);
        (await _snapshots.GetBySprintIdAsync("sprint-1"))!.InitialCommittedPoints.Should().Be(0);
        await ReceivedScopeCreepAlerts(0);

        // MS2 starts the sprint with the 16 SP; +2 SP stays under the 20 % threshold.
        Ms2Says(SprintLifecycleStatus.Active, 16);
        await Assign("i-4", 2);
        var snapshot = await _snapshots.GetBySprintIdAsync("sprint-1");
        snapshot!.InitialCommittedPoints.Should().Be(16);
        snapshot.TotalStoryPoints.Should().Be(18);
        await ReceivedScopeCreepAlerts(0);

        // +5 SP more (23 > 16 * 1.2): real scope creep.
        await Assign("i-5", 5);
        await ReceivedScopeCreepAlerts(1);
    }

    [Fact]
    public async Task AQuery_RepairsASnapshotThatTookTheFirstIssueAsCommitment()
    {
        await _snapshots.UpsertSnapshotAsync(new SprintSnapshotDocument
        {
            SprintId = "sprint-1", ProjectId = "project-1", Status = SnapshotStatus.Active,
            StartDate = Today.AddDays(-1), EndDate = Today.AddDays(13),
            InitialCommittedPoints = 3, TotalStoryPoints = 16, CompletedStoryPoints = 14, RemainingStoryPoints = 2,
        });
        Ms2Says(SprintLifecycleStatus.Active, 16);

        var reply = await Queries().ComputeReplyAsync(
            new AnalyticsQueryDto { SprintId = "sprint-1", ProjectId = "project-1", QueryType = "BURNDOWN" }, "corr");

        (await _snapshots.GetBySprintIdAsync("sprint-1"))!.InitialCommittedPoints.Should().Be(16);
        reply.Diagnostics.Should().NotContain(d => d.Contains("Scope creep"));
        reply.HealthStatus.Should().Be("HEALTHY");
        reply.HealthScore.Should().Be(100.0);
    }
}
