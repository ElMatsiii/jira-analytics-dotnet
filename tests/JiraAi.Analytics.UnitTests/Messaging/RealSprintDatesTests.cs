namespace JiraAi.Analytics.UnitTests.Messaging;

using FluentAssertions;
using JiraAi.Analytics.Contracts.Dtos;
using JiraAi.Analytics.Domain.Models;
using JiraAi.Analytics.Domain.Services;
using JiraAi.Analytics.Infrastructure.Grpc;
using JiraAi.Analytics.Infrastructure.Messaging;
using JiraAi.Analytics.Infrastructure.Persistence;
using JiraAi.Analytics.UnitTests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

/// <summary>Snapshots and replies use the sprint dates/status owned by MS2 (read over gRPC).</summary>
public class RealSprintDatesTests
{
    private readonly InMemorySnapshotRepository _snapshots = new();
    private readonly InMemoryVelocityRepository _velocity = new();
    private readonly FakeSprintDirectory _directory = new();
    private static readonly DateTime Today = DateTime.UtcNow.Date;

    private IssuesEventProcessor CreateEventProcessor()
    {
        var alerts = Substitute.For<IAlertClientService>();
        alerts.TriggerSprintAlertAsync(default!, default, default, default!, default!, default)
            .ReturnsForAnyArgs(Task.FromResult(AlertResult.Succeeded("a")));
        return new IssuesEventProcessor(
            _snapshots, new RiskDetectionEngine(), alerts,
            TestSprintSync.Create(_snapshots, _velocity, _directory),
            NullLogger<IssuesEventProcessor>.Instance);
    }

    private AnalyticsQueryProcessor CreateQueryProcessor() =>
        new(_snapshots, _velocity, new BurndownCalculator(), new VelocityCalculator(), new HealthScoreCalculator(),
            new RiskDetectionEngine(), Substitute.For<IKafkaProducerWrapper>(), Options.Create(new KafkaOptions()),
            NullLogger<AnalyticsQueryProcessor>.Instance,
            TestSprintSync.Create(_snapshots, _velocity, _directory));

    private static IssueEventEnvelope Created(string sprintId, int points) => new()
    {
        EventId = Guid.NewGuid().ToString(),
        EventType = "ISSUE_CREATED",
        Timestamp = DateTime.UtcNow.ToString("o"),
        Payload = new IssueEventPayload
        {
            Id = Guid.NewGuid().ToString(), Key = "PROJ-1", ProjectId = "project-1", SprintId = sprintId,
            Status = "TODO", StoryPoints = points
        }
    };

    [Fact]
    public async Task IssueEvent_BuildsSnapshotAndIdealLineFromRealSprintDates()
    {
        // Sprint started 5 days ago and lasts 10 days (MS2 data), not "first event + 14 days".
        _directory.With(new SprintInfo("sprint-real", "project-1", "Sprint Real",
            Today.AddDays(-5), Today.AddDays(5), SprintLifecycleStatus.Active));

        await CreateEventProcessor().ProcessEnvelopeAsync(Created("sprint-real", 20));

        var snapshot = await _snapshots.GetBySprintIdAsync("sprint-real");
        snapshot!.StartDate.Should().Be(Today.AddDays(-5));
        snapshot.EndDate.Should().Be(Today.AddDays(5));
        snapshot.SprintName.Should().Be("Sprint Real");
        var today = snapshot.DailyBurndown.Single();
        today.DayIndex.Should().Be(5);
        today.IdealRemaining.Should().Be(10, "20 SP over 10 days, 5 days elapsed");
    }

    [Fact]
    public async Task IssueEvent_WithMs2Unreachable_KeepsWorkingWithProvisionalDates()
    {
        await CreateEventProcessor().ProcessEnvelopeAsync(Created("sprint-offline", 14));

        var snapshot = await _snapshots.GetBySprintIdAsync("sprint-offline");
        snapshot!.StartDate.Date.Should().Be(Today);
        snapshot.TotalStoryPoints.Should().Be(14);
    }

    [Fact]
    public async Task Query_ForUntrackedSprint_UsesRealDatesWithoutPersisting()
    {
        _directory.With(new SprintInfo("sprint-new", "project-1", "Sprint Nuevo",
            Today.AddDays(2), Today.AddDays(9), SprintLifecycleStatus.Planned));

        var reply = await CreateQueryProcessor().ComputeReplyAsync(
            new AnalyticsQueryDto { SprintId = "sprint-new", ProjectId = "project-1", QueryType = "BURNDOWN" }, "corr-1");

        reply.Status.Should().Be("SUCCESS");
        reply.BurndownData.Should().HaveCount(8, "7 days between start and end, both included");
        reply.BurndownData!.First().Date.Should().Be(Today.AddDays(2).ToString("yyyy-MM-dd"));
        (await _snapshots.GetBySprintIdAsync("sprint-new")).Should().BeNull();
    }

    [Fact]
    public async Task Query_AfterMs2CompletedTheSprint_RecordsVelocityAndUsesIt()
    {
        // Snapshot built while the sprint was running...
        _directory.With(new SprintInfo("sprint-done", "project-1", "Sprint Hecho",
            Today.AddDays(-14), Today.AddDays(-1), SprintLifecycleStatus.Active));
        await CreateEventProcessor().ProcessEnvelopeAsync(Created("sprint-done", 13));
        var snapshot = await _snapshots.GetBySprintIdAsync("sprint-done");
        snapshot!.CompletedStoryPoints = 13;
        await _snapshots.UpsertSnapshotAsync(snapshot);

        // ...then MS2 completes it (MS3 receives no event for that).
        _directory.With(new SprintInfo("sprint-done", "project-1", "Sprint Hecho",
            Today.AddDays(-14), Today.AddDays(-1), SprintLifecycleStatus.Completed));

        var reply = await CreateQueryProcessor().ComputeReplyAsync(
            new AnalyticsQueryDto { SprintId = "sprint-done", ProjectId = "project-1", QueryType = "VELOCITY" }, "corr-2");

        (await _snapshots.GetBySprintIdAsync("sprint-done"))!.Status.Should().Be(SnapshotStatus.Completed);
        var history = await _velocity.GetRecentCompletedSprintsAsync("project-1");
        history.Should().ContainSingle(v => v.SprintId == "sprint-done" && v.CompletedStoryPoints == 13);
        reply.Velocity.Should().Be(13, "rolling velocity now comes from the recorded history");
    }

    [Fact]
    public async Task Query_RunningSprintWithoutClosedSprints_ReportsZeroVelocity()
    {
        _directory.With(new SprintInfo("sprint-run", "project-1", "Sprint en curso",
            Today.AddDays(-3), Today.AddDays(10), SprintLifecycleStatus.Active));
        await CreateEventProcessor().ProcessEnvelopeAsync(Created("sprint-run", 6));
        var snapshot = await _snapshots.GetBySprintIdAsync("sprint-run");
        snapshot!.CompletedStoryPoints = 6; // done in the current sprint, not a closed-sprint velocity
        await _snapshots.UpsertSnapshotAsync(snapshot);

        var reply = await CreateQueryProcessor().ComputeReplyAsync(
            new AnalyticsQueryDto { SprintId = "sprint-run", ProjectId = "project-1", QueryType = "BURNDOWN" }, "corr-3");

        reply.Velocity.Should().Be(0, "the average only counts closed sprints");
        reply.CompletedPoints.Should().Be(6);
    }

    [Fact]
    public void ToDto_CopiesEveryBurndownField()
    {
        var dto = AnalyticsQueryProcessor.ToDto(new BurndownPoint("2026-11-16", 3, 12.5, 14, 2), actualWhenUnknown: 99);

        dto.Should().BeEquivalentTo(new BurndownPointDto
        {
            Date = "2026-11-16", DayIndex = 3, IdealRemaining = 12.5, ActualRemaining = 14, CompletedOnDay = 2
        });
    }

    [Fact]
    public void ToDtos_FutureDaysCarryTheLastKnownValueUntilTheContractAllowsNull()
    {
        var points = new List<BurndownPoint>
        {
            new("2026-11-16", 0, 20, 20, null),
            new("2026-11-17", 1, 15, 12, 8),
            new("2026-11-18", 2, 10, null, null),
            new("2026-11-19", 3, 5, null, null)
        };

        var dtos = AnalyticsQueryProcessor.ToDtos(points, currentRemaining: 30);

        dtos.Select(d => d.ActualRemaining).Should().Equal(20, 12, 12, 12);
        AnalyticsQueryProcessor.ToDtos(new List<BurndownPoint> { new("2026-11-20", 0, 8, null, null) }, 7)
            .Single().ActualRemaining.Should().Be(7, "with no known day yet, the current remaining is used");
    }
}
