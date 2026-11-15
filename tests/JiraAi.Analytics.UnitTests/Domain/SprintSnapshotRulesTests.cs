namespace JiraAi.Analytics.UnitTests.Domain;

using FluentAssertions;
using JiraAi.Analytics.Domain.Models;
using JiraAi.Analytics.Domain.Services;
using Xunit;

public class SprintSnapshotRulesTests
{
    private static readonly DateTime Now = new(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Start = new(2026, 11, 16, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End = new(2026, 11, 29, 23, 59, 59, DateTimeKind.Utc);

    private static SprintInfo Info(SprintLifecycleStatus status = SprintLifecycleStatus.Active) =>
        new("sprint-1", "project-1", "Sprint Pagos", Start, End, status);

    [Fact]
    public void NewSnapshot_UsesMs2DatesNameAndStatus()
    {
        var snapshot = SprintSnapshotRules.NewSnapshot("sprint-1", "project-from-event", Info(SprintLifecycleStatus.Planned), Now);

        snapshot.StartDate.Should().Be(Start);
        snapshot.EndDate.Should().Be(End);
        snapshot.SprintName.Should().Be("Sprint Pagos");
        snapshot.ProjectId.Should().Be("project-1");
        snapshot.Status.Should().Be(SnapshotStatus.Planned);
    }

    [Fact]
    public void NewSnapshot_WithoutMs2_UsesProvisionalWindow()
    {
        var snapshot = SprintSnapshotRules.NewSnapshot("sprint-1", "project-1", null, Now);

        snapshot.StartDate.Should().Be(Now);
        snapshot.EndDate.Should().Be(Now.AddDays(SprintSnapshotRules.FallbackSprintDays));
        snapshot.Status.Should().Be(SnapshotStatus.Active);
        snapshot.SprintName.Should().Be("Sprint sprint-1");
    }

    [Fact]
    public void ApplySprintInfo_ReportsWhetherSomethingChanged()
    {
        var snapshot = SprintSnapshotRules.NewSnapshot("sprint-1", "project-1", null, Now);

        SprintSnapshotRules.ApplySprintInfo(snapshot, Info()).Should().BeTrue();
        snapshot.StartDate.Should().Be(Start);
        SprintSnapshotRules.ApplySprintInfo(snapshot, Info()).Should().BeFalse("nothing changed the second time");
    }

    [Fact]
    public void ApplySprintInfo_DoesNotReopenAClosedSnapshotNorCloseItByItself()
    {
        var closed = SprintSnapshotRules.NewSnapshot("sprint-1", "project-1", Info(), Now);
        closed.Status = SnapshotStatus.Completed;
        SprintSnapshotRules.ApplySprintInfo(closed, Info(SprintLifecycleStatus.Active));
        closed.Status.Should().Be(SnapshotStatus.Completed);

        var open = SprintSnapshotRules.NewSnapshot("sprint-1", "project-1", Info(), Now);
        SprintSnapshotRules.ApplySprintInfo(open, Info(SprintLifecycleStatus.Completed));
        open.Status.Should().Be(SnapshotStatus.Active, "closing (with velocity) is a separate step");
    }

    [Fact]
    public void ShouldRecordVelocity_OnlyOnceWhenMs2ReportsCompleted()
    {
        var snapshot = SprintSnapshotRules.NewSnapshot("sprint-1", "project-1", Info(), Now);

        SprintSnapshotRules.ShouldRecordVelocity(snapshot, Info(SprintLifecycleStatus.Active)).Should().BeFalse();
        SprintSnapshotRules.ShouldRecordVelocity(snapshot, Info(SprintLifecycleStatus.Completed)).Should().BeTrue();

        snapshot.Status = SnapshotStatus.Completed;
        SprintSnapshotRules.ShouldRecordVelocity(snapshot, Info(SprintLifecycleStatus.Completed)).Should().BeFalse();
    }

    [Fact]
    public void CloseAndBuildVelocityRecord_ClosesSnapshotWithCommittedAndCompletedPoints()
    {
        var snapshot = SprintSnapshotRules.NewSnapshot("sprint-1", "project-1", Info(), Now);
        snapshot.TotalStoryPoints = 21;
        snapshot.CompletedStoryPoints = 13;

        var record = SprintSnapshotRules.CloseAndBuildVelocityRecord(snapshot, Info(SprintLifecycleStatus.Completed), Now);

        snapshot.Status.Should().Be(SnapshotStatus.Completed);
        record.ProjectId.Should().Be("project-1");
        record.SprintId.Should().Be("sprint-1");
        record.SprintName.Should().Be("Sprint Pagos");
        record.CommittedStoryPoints.Should().Be(21);
        record.CompletedStoryPoints.Should().Be(13);
        record.CompletedDate.Should().Be(End);
    }

    [Fact]
    public void DurationDays_UsesCalendarDaysWithMinimumOne()
    {
        var snapshot = SprintSnapshotRules.NewSnapshot("sprint-1", "project-1", Info(), Now);
        SprintSnapshotRules.DurationDays(snapshot).Should().Be(13);

        snapshot.EndDate = snapshot.StartDate;
        SprintSnapshotRules.DurationDays(snapshot).Should().Be(1);
    }

    [Theory]
    [InlineData(SprintLifecycleStatus.Planned, "PLANNED")]
    [InlineData(SprintLifecycleStatus.Active, "ACTIVE")]
    [InlineData(SprintLifecycleStatus.Completed, "COMPLETED")]
    [InlineData(SprintLifecycleStatus.Cancelled, "CANCELLED")]
    [InlineData(SprintLifecycleStatus.Unknown, "ACTIVE")]
    public void SprintInfo_StatusName_MapsToSnapshotStatus(SprintLifecycleStatus status, string expected)
    {
        Info(status).StatusName.Should().Be(expected);
        SnapshotStatus.IsClosed(expected).Should().Be(expected is "COMPLETED" or "CANCELLED");
    }
}
