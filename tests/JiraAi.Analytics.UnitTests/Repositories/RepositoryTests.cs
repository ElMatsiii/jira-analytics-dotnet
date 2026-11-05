namespace JiraAi.Analytics.UnitTests.Repositories;

using FluentAssertions;
using JiraAi.Analytics.Domain.Models;
using JiraAi.Analytics.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using NSubstitute;
using Xunit;

public class RepositoryTests
{
    [Fact]
    public async Task InMemorySnapshotRepository_T1_F20_01_02_UpsertAndGet_WorksCorrectly()
    {
        var repo = new InMemorySnapshotRepository();
        var snapshot = new SprintSnapshotDocument
        {
            SprintId = "sprint-100",
            ProjectId = "proj-1",
            SprintName = "Sprint 100",
            TotalStoryPoints = 40,
            CompletedStoryPoints = 15,
            RemainingStoryPoints = 25
        };

        await repo.UpsertSnapshotAsync(snapshot);

        var retrieved = await repo.GetBySprintIdAsync("sprint-100");
        retrieved.Should().NotBeNull();
        retrieved!.SprintId.Should().Be("sprint-100");
        retrieved.TotalStoryPoints.Should().Be(40);
        retrieved.Version.Should().BeGreaterThan(1);
    }

    [Fact]
    public async Task InMemorySnapshotRepository_T2_F20_03_NonExistentSprint_ReturnsNull()
    {
        var repo = new InMemorySnapshotRepository();
        var result = await repo.GetBySprintIdAsync("does-not-exist");
        result.Should().BeNull();

        var emptyResult = await repo.GetBySprintIdAsync("");
        emptyResult.Should().BeNull();
    }

    [Fact]
    public async Task InMemorySnapshotRepository_T1_F20_03_AppendDailyBurndown_UpdatesList()
    {
        var repo = new InMemorySnapshotRepository();
        var snapshot = new SprintSnapshotDocument
        {
            SprintId = "sprint-burndown",
            TotalStoryPoints = 50,
            RemainingStoryPoints = 50
        };
        await repo.UpsertSnapshotAsync(snapshot);

        var burndownRecord = new DailyBurndownRecord
        {
            Date = "2026-10-01",
            DayIndex = 0,
            IdealRemaining = 50,
            ActualRemaining = 45,
            CompletedOnDay = 5
        };

        await repo.AppendDailyBurndownAsync("sprint-burndown", burndownRecord);

        var retrieved = await repo.GetBySprintIdAsync("sprint-burndown");
        retrieved!.DailyBurndown.Should().ContainSingle();
        retrieved.DailyBurndown[0].Date.Should().Be("2026-10-01");
        retrieved.DailyBurndown[0].ActualRemaining.Should().Be(45);
    }

    [Fact]
    public async Task InMemorySnapshotRepository_GetActiveSnapshots_ReturnsOnlyActive()
    {
        var repo = new InMemorySnapshotRepository();
        await repo.UpsertSnapshotAsync(new SprintSnapshotDocument { SprintId = "s-act", Status = "ACTIVE" });
        await repo.UpsertSnapshotAsync(new SprintSnapshotDocument { SprintId = "s-cmp", Status = "COMPLETED" });

        var active = await repo.GetActiveSnapshotsAsync();
        active.Should().ContainSingle(s => s.SprintId == "s-act");
    }

    [Fact]
    public async Task InMemoryVelocityRepository_T1_F20_04_RecordAndGet_ReturnsOrderedHistory()
    {
        var repo = new InMemoryVelocityRepository();
        await repo.RecordVelocityAsync(new VelocityRecordDocument
        {
            ProjectId = "proj-alpha",
            SprintId = "s-1",
            CompletedStoryPoints = 20,
            CompletedDate = DateTime.UtcNow.AddDays(-20)
        });
        await repo.RecordVelocityAsync(new VelocityRecordDocument
        {
            ProjectId = "proj-alpha",
            SprintId = "s-2",
            CompletedStoryPoints = 35,
            CompletedDate = DateTime.UtcNow.AddDays(-5)
        });

        var history = await repo.GetRecentCompletedSprintsAsync("proj-alpha", limit: 3);
        history.Should().HaveCount(2);
        history[0].SprintId.Should().Be("s-2"); // Most recent first
    }

    [Fact]
    public async Task InMemoryAlertRepository_RecordAndGetLastAlert_AuditsDispatches()
    {
        var repo = new InMemoryAlertRepository();
        var alert = new SprintAlertDocument
        {
            SprintId = "sprint-alert-test",
            AlertType = "ScopeCreep",
            Severity = "Warning",
            Message = "Scope creep detected",
            DispatchedAt = DateTime.UtcNow
        };

        await repo.RecordAlertAsync(alert);

        var lastTime = await repo.GetLastAlertTimeAsync("sprint-alert-test", "ScopeCreep");
        lastTime.Should().NotBeNull();
        lastTime.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));

        var missing = await repo.GetLastAlertTimeAsync("sprint-alert-test", "VelocityDeficit");
        missing.Should().BeNull();
    }

    [Fact]
    public async Task MongoSnapshotRepository_T2_F20_03_EmptySprintId_ReturnsNullWithoutQueryingDb()
    {
        var mockContext = Substitute.For<IMongoDbContext>();
        var repo = new MongoSnapshotRepository(mockContext, NullLogger<MongoSnapshotRepository>.Instance);

        var result = await repo.GetBySprintIdAsync("");
        result.Should().BeNull();
    }

    [Fact]
    public async Task MongoSnapshotRepository_UpsertSnapshot_CallsReplaceOneAsync()
    {
        var mockContext = Substitute.For<IMongoDbContext>();
        var mockCollection = Substitute.For<IMongoCollection<SprintSnapshotDocument>>();
        mockContext.Snapshots.Returns(mockCollection);

        var repo = new MongoSnapshotRepository(mockContext, NullLogger<MongoSnapshotRepository>.Instance);
        var snapshot = new SprintSnapshotDocument { SprintId = "s-upsert-1" };

        await repo.UpsertSnapshotAsync(snapshot);

        await mockCollection.Received(1).ReplaceOneAsync(
            Arg.Any<FilterDefinition<SprintSnapshotDocument>>(),
            snapshot,
            Arg.Any<ReplaceOptions>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MongoVelocityRepository_RecordVelocity_CallsReplaceOneAsync()
    {
        var mockContext = Substitute.For<IMongoDbContext>();
        var mockCollection = Substitute.For<IMongoCollection<VelocityRecordDocument>>();
        mockContext.Velocity.Returns(mockCollection);

        var repo = new MongoVelocityRepository(mockContext, NullLogger<MongoVelocityRepository>.Instance);
        var record = new VelocityRecordDocument { ProjectId = "p-1", SprintId = "s-1", CompletedStoryPoints = 20 };

        await repo.RecordVelocityAsync(record);

        await mockCollection.Received(1).ReplaceOneAsync(
            Arg.Any<FilterDefinition<VelocityRecordDocument>>(),
            record,
            Arg.Any<ReplaceOptions>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MongoAlertRepository_RecordAlert_CallsInsertOneAsync()
    {
        var mockContext = Substitute.For<IMongoDbContext>();
        var mockCollection = Substitute.For<IMongoCollection<SprintAlertDocument>>();
        mockContext.Alerts.Returns(mockCollection);

        var repo = new MongoAlertRepository(mockContext, NullLogger<MongoAlertRepository>.Instance);
        var alert = new SprintAlertDocument { SprintId = "s-1", AlertType = "ScopeCreep" };

        await repo.RecordAlertAsync(alert);

        await mockCollection.Received(1).InsertOneAsync(
            alert,
            Arg.Any<InsertOneOptions>(),
            Arg.Any<CancellationToken>());
    }
}
