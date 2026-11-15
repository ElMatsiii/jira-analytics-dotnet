namespace JiraAi.Analytics.UnitTests.Messaging;

using System.Text.Json;
using FluentAssertions;
using JiraAi.Analytics.Contracts.Dtos;
using JiraAi.Analytics.Domain.Models;
using JiraAi.Analytics.Domain.Services;
using JiraAi.Analytics.Infrastructure.Messaging;
using JiraAi.Analytics.Infrastructure.Persistence;
using JiraAi.Analytics.UnitTests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

/// <summary>analytics.reply carries MS3's healthStatus, velocitySampleSize and null future days.</summary>
public class HealthStatusReplyTests
{
    private readonly InMemorySnapshotRepository _snapshots = new();
    private readonly InMemoryVelocityRepository _velocity = new();
    private readonly FakeSprintDirectory _directory = new();
    private static readonly DateTime Today = DateTime.UtcNow.Date;

    private AnalyticsQueryProcessor CreateQueryProcessor() =>
        new(_snapshots, _velocity, new BurndownCalculator(), new VelocityCalculator(), new HealthScoreCalculator(),
            new RiskDetectionEngine(), Substitute.For<IKafkaProducerWrapper>(), Options.Create(new KafkaOptions()),
            NullLogger<AnalyticsQueryProcessor>.Instance,
            TestSprintSync.Create(_snapshots, _velocity, _directory));

    private Task<AnalyticsReplyDto> Query(string sprintId) => CreateQueryProcessor().ComputeReplyAsync(
        new AnalyticsQueryDto { SprintId = sprintId, ProjectId = "project-1", QueryType = "BURNDOWN" }, "corr");

    private async Task SeedRunningSprint(string sprintId, int total, int completed)
    {
        _directory.With(new SprintInfo(sprintId, "project-1", "Sprint", Today.AddDays(-5), Today.AddDays(5),
            SprintLifecycleStatus.Active));
        await _snapshots.UpsertSnapshotAsync(new SprintSnapshotDocument
        {
            SprintId = sprintId, ProjectId = "project-1", Status = SnapshotStatus.Active,
            StartDate = Today.AddDays(-5), EndDate = Today.AddDays(5),
            InitialCommittedPoints = total, TotalStoryPoints = total,
            CompletedStoryPoints = completed, RemainingStoryPoints = total - completed
        });
    }

    [Fact]
    public async Task PlannedSprint_IsNotStartedWithEveryDayInTheFuture()
    {
        _directory.With(new SprintInfo("sprint-next", "project-1", "Próximo", Today.AddDays(2), Today.AddDays(9),
            SprintLifecycleStatus.Planned));

        var reply = await Query("sprint-next");

        reply.HealthStatus.Should().Be("NOT_STARTED");
        reply.VelocitySampleSize.Should().Be(0);
        reply.BurndownData.Should().OnlyContain(p => p.ActualRemaining == null, "none of its days has happened");
    }

    [Fact]
    public async Task RunningSprintOnPace_IsHealthy_AndTheRealLineStopsToday()
    {
        await SeedRunningSprint("sprint-ok", total: 20, completed: 10);

        var reply = await Query("sprint-ok");

        reply.HealthScore.Should().BeGreaterThanOrEqualTo(SprintHealthRules.HealthyMinScore);
        reply.HealthStatus.Should().Be("HEALTHY");
        var todayIndex = reply.BurndownData.FindIndex(p => p.Date == Today.ToString("yyyy-MM-dd"));
        reply.BurndownData.Take(todayIndex + 1).Should().OnlyContain(p => p.ActualRemaining != null);
        reply.BurndownData.Skip(todayIndex + 1).Should().NotBeEmpty().And.OnlyContain(p => p.ActualRemaining == null);
    }

    [Fact]
    public async Task RunningSprintFarBehind_IsCritical()
    {
        await SeedRunningSprint("sprint-late", total: 20, completed: 0);
        var snapshot = await _snapshots.GetBySprintIdAsync("sprint-late");
        snapshot!.StartDate = Today.AddDays(-9);
        snapshot.EndDate = Today.AddDays(1);
        snapshot.TotalStoryPoints = 30; // scope creep on top of no progress
        snapshot.RemainingStoryPoints = 30;
        await _snapshots.UpsertSnapshotAsync(snapshot);
        _directory.With(new SprintInfo("sprint-late", "project-1", "Sprint", Today.AddDays(-9), Today.AddDays(1),
            SprintLifecycleStatus.Active));

        var reply = await Query("sprint-late");

        reply.HealthScore.Should().BeLessThan(SprintHealthRules.AtRiskMinScore);
        reply.HealthStatus.Should().Be("CRITICAL");
    }

    [Fact]
    public async Task RunningSprintWithoutPoints_IsNoScope()
    {
        await SeedRunningSprint("sprint-empty", total: 0, completed: 0);

        var reply = await Query("sprint-empty");

        reply.HealthStatus.Should().Be("NO_SCOPE");
    }

    [Fact]
    public async Task VelocitySampleSize_CountsUpToThreeClosedSprints()
    {
        await SeedRunningSprint("sprint-now", total: 10, completed: 5);
        for (var i = 1; i <= 4; i++)
        {
            await _velocity.RecordVelocityAsync(new VelocityRecordDocument
            {
                ProjectId = "project-1", SprintId = $"closed-{i}", CompletedStoryPoints = 10 * i,
                CompletedDate = Today.AddDays(-30 * i)
            });
        }

        var reply = await Query("sprint-now");

        reply.VelocitySampleSize.Should().Be(3);
        reply.Velocity.Should().Be(20, "average of the 3 most recent closed sprints: 10, 20 and 30");
    }

    [Fact]
    public void IssueEvent_ParentIdIsRead_AndOptional()
    {
        var withParent = JsonSerializer.Deserialize<IssueEventEnvelope>(
            """{"eventId":"e1","eventType":"ISSUE_CREATED","payload":{"id":"i1","parentId":"epic-1"}}""");
        var withoutParent = JsonSerializer.Deserialize<IssueEventEnvelope>(
            """{"eventId":"e2","eventType":"ISSUE_CREATED","payload":{"id":"i2"}}""");

        withParent!.Payload.ParentId.Should().Be("epic-1");
        withoutParent!.Payload.ParentId.Should().BeNull();
    }
}
