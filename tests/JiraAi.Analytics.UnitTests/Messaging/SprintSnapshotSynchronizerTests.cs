namespace JiraAi.Analytics.UnitTests.Messaging;

using FluentAssertions;
using JiraAi.Analytics.Domain.Models;
using JiraAi.Analytics.Domain.Repositories;
using JiraAi.Analytics.Infrastructure.Persistence;
using JiraAi.Analytics.Infrastructure.Sprints;
using JiraAi.Analytics.UnitTests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

public class SprintSnapshotSynchronizerTests
{
    private static readonly DateTime Now = new(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Start = new(2026, 11, 16, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End = new(2026, 11, 29, 23, 59, 59, DateTimeKind.Utc);

    private readonly InMemorySnapshotRepository _snapshots = new();
    private readonly InMemoryVelocityRepository _velocity = new();
    private readonly FakeSprintDirectory _directory = new();

    private SprintSnapshotSynchronizer CreateSut() => TestSprintSync.Create(_snapshots, _velocity, _directory, () => Now);

    private static SprintInfo Sprint(string id, SprintLifecycleStatus status, string project = "project-1") =>
        new(id, project, $"Sprint {id.ToUpperInvariant()}", Start, End, status);

    [Fact]
    public async Task CreateSnapshot_UsesRealDatesFromMs2()
    {
        _directory.With(Sprint("s1", SprintLifecycleStatus.Active));

        var snapshot = await CreateSut().CreateSnapshotAsync("s1", "project-1");

        snapshot.StartDate.Should().Be(Start);
        snapshot.EndDate.Should().Be(End);
        snapshot.SprintName.Should().Be("Sprint S1");
        snapshot.Status.Should().Be(SnapshotStatus.Active);
    }

    [Fact]
    public async Task CreateSnapshot_ForAnAlreadyCompletedSprint_ClosesItAndRecordsVelocity()
    {
        _directory.With(Sprint("s1", SprintLifecycleStatus.Completed));

        var snapshot = await CreateSut().CreateSnapshotAsync("s1", "project-1");

        snapshot.Status.Should().Be(SnapshotStatus.Completed);
        (await _velocity.GetRecentCompletedSprintsAsync("project-1")).Should().ContainSingle(v => v.SprintId == "s1");
    }

    [Fact]
    public async Task CreateSnapshot_WhenMs2IsUnreachable_UsesProvisionalWindow()
    {
        var snapshot = await CreateSut().CreateSnapshotAsync("unknown", "project-1");

        snapshot.StartDate.Should().Be(Now);
        snapshot.EndDate.Should().Be(Now.AddDays(14));
    }

    [Fact]
    public async Task Synchronize_UpdatesDatesAndReportsChange()
    {
        var snapshot = new SprintSnapshotDocument { SprintId = "s1", ProjectId = "project-1", StartDate = Now, EndDate = Now.AddDays(14) };
        _directory.With(Sprint("s1", SprintLifecycleStatus.Planned));

        (await CreateSut().SynchronizeAsync(snapshot)).Should().BeTrue();

        snapshot.StartDate.Should().Be(Start);
        snapshot.Status.Should().Be(SnapshotStatus.Planned);
        (await CreateSut().SynchronizeAsync(snapshot)).Should().BeFalse("already aligned");
    }

    [Fact]
    public async Task Synchronize_WhenMs2IsUnreachable_KeepsSnapshotUntouched()
    {
        var snapshot = new SprintSnapshotDocument { SprintId = "s1", StartDate = Now, EndDate = Now.AddDays(14) };

        (await CreateSut().SynchronizeAsync(snapshot)).Should().BeFalse();
        snapshot.StartDate.Should().Be(Now);
    }

    [Fact]
    public async Task Synchronize_CompletedSprint_RecordsVelocityOnce()
    {
        var snapshot = new SprintSnapshotDocument
        {
            SprintId = "s1", ProjectId = "project-1", Status = SnapshotStatus.Active,
            TotalStoryPoints = 20, CompletedStoryPoints = 15
        };
        _directory.With(Sprint("s1", SprintLifecycleStatus.Completed));
        var sut = CreateSut();

        (await sut.SynchronizeAsync(snapshot)).Should().BeTrue();
        (await sut.SynchronizeAsync(snapshot)).Should().BeFalse("closing happens once");

        snapshot.Status.Should().Be(SnapshotStatus.Completed);
        var history = await _velocity.GetRecentCompletedSprintsAsync("project-1");
        history.Should().ContainSingle();
        history[0].CompletedStoryPoints.Should().Be(15);
        history[0].CommittedStoryPoints.Should().Be(20);
        history[0].CompletedDate.Should().Be(End);
    }

    [Fact]
    public async Task Synchronize_CancelledSprint_IsClosedWithoutVelocity()
    {
        var snapshot = new SprintSnapshotDocument { SprintId = "s1", ProjectId = "project-1", Status = SnapshotStatus.Active };
        _directory.With(Sprint("s1", SprintLifecycleStatus.Cancelled));

        (await CreateSut().SynchronizeAsync(snapshot)).Should().BeTrue();

        snapshot.Status.Should().Be(SnapshotStatus.Cancelled);
        (await _velocity.GetRecentCompletedSprintsAsync("project-1")).Should().BeEmpty();
    }

    [Fact]
    public async Task Reconcile_ClosesCompletedSprintsAndPersistsThem()
    {
        await _snapshots.UpsertSnapshotAsync(new SprintSnapshotDocument { SprintId = "done", ProjectId = "project-1", Status = SnapshotStatus.Active, CompletedStoryPoints = 8 });
        await _snapshots.UpsertSnapshotAsync(new SprintSnapshotDocument { SprintId = "running", ProjectId = "project-1", Status = SnapshotStatus.Active });
        await _snapshots.UpsertSnapshotAsync(new SprintSnapshotDocument { SprintId = "old", ProjectId = "project-1", Status = SnapshotStatus.Completed });
        _directory.With(Sprint("done", SprintLifecycleStatus.Completed)).With(Sprint("running", SprintLifecycleStatus.Active));

        var closed = await CreateSut().ReconcileOpenSnapshotsAsync();

        closed.Should().Be(1);
        (await _snapshots.GetBySprintIdAsync("done"))!.Status.Should().Be(SnapshotStatus.Completed);
        (await _snapshots.GetBySprintIdAsync("running"))!.StartDate.Should().Be(Start, "open sprints get their real dates persisted");
        _directory.Calls.Should().Be(2, "closed snapshots are not queried");
        (await _velocity.GetRecentCompletedSprintsAsync("project-1")).Should().ContainSingle(v => v.SprintId == "done");
    }

    [Fact]
    public async Task Reconcile_ContinuesWhenOneSnapshotFails()
    {
        var snapshots = Substitute.For<ISnapshotRepository>();
        snapshots.GetOpenSnapshotsAsync(Arg.Any<CancellationToken>()).Returns(new List<SprintSnapshotDocument>
        {
            new() { SprintId = "a", ProjectId = "project-1", Status = SnapshotStatus.Active },
            new() { SprintId = "b", ProjectId = "project-1", Status = SnapshotStatus.Active }
        });
        snapshots.UpsertSnapshotAsync(Arg.Is<SprintSnapshotDocument>(s => s.SprintId == "a"), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("mongo down"));
        _directory.With(Sprint("a", SprintLifecycleStatus.Completed)).With(Sprint("b", SprintLifecycleStatus.Completed));
        var sut = new SprintSnapshotSynchronizer(_directory, snapshots, _velocity, NullLogger<SprintSnapshotSynchronizer>.Instance, () => Now);

        var closed = await sut.ReconcileOpenSnapshotsAsync();

        closed.Should().Be(1, "the failure on 'a' is logged and 'b' is still closed");
    }
}
