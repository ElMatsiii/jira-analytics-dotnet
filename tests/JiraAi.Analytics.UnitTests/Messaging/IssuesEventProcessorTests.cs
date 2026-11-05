namespace JiraAi.Analytics.UnitTests.Messaging;

using System.Text.Json;
using FluentAssertions;
using JiraAi.Analytics.Contracts.Dtos;
using JiraAi.Analytics.Domain.Models;
using JiraAi.Analytics.Domain.Services;
using JiraAi.Analytics.Infrastructure.Grpc;
using JiraAi.Analytics.Infrastructure.Messaging;
using JiraAi.Analytics.Infrastructure.Persistence;
using JiraAi.Contracts.Sprints.V1;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

public class IssuesEventProcessorTests
{
    private readonly InMemorySnapshotRepository _snapshotRepo = new();
    private readonly IRiskDetectionEngine _riskEngine = new RiskDetectionEngine();
    private readonly IAlertClientService _alertClient = Substitute.For<IAlertClientService>();
    private readonly IssuesEventProcessor _sut;

    public IssuesEventProcessorTests()
    {
        _alertClient.TriggerSprintAlertAsync(
            Arg.Any<string>(), Arg.Any<AlertType>(), Arg.Any<AlertSeverity>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(AlertResult.Succeeded("alert-123")));

        _sut = new IssuesEventProcessor(
            _snapshotRepo,
            _riskEngine,
            _alertClient,
            NullLogger<IssuesEventProcessor>.Instance);
    }

    [Fact]
    public async Task ProcessEnvelope_T1_F21_01_IssueCreated_IncrementsSprintPoints()
    {
        var envelope = new IssueEventEnvelope
        {
            EventId = "evt-create-1",
            EventType = "ISSUE_CREATED",
            Timestamp = DateTime.UtcNow.ToString("o"),
            Payload = new IssueEventPayload
            {
                Id = "issue-1",
                Key = "PROJ-1",
                ProjectId = "proj-1",
                SprintId = "sprint-10",
                Status = "TODO",
                StoryPoints = 8
            }
        };

        var result = await _sut.ProcessEnvelopeAsync(envelope);
        result.Should().BeTrue();

        var snapshot = await _snapshotRepo.GetBySprintIdAsync("sprint-10");
        snapshot.Should().NotBeNull();
        snapshot!.TotalStoryPoints.Should().Be(8);
        snapshot.RemainingStoryPoints.Should().Be(8);
        snapshot.CompletedStoryPoints.Should().Be(0);
        snapshot.TrackedIssues.Should().ContainKey("issue-1");
    }

    [Fact]
    public async Task ProcessEnvelope_T1_F21_02_IssueStatusChanged_ToDone_IncrementsCompletedPoints()
    {
        // 1. Create issue
        await _sut.ProcessEnvelopeAsync(new IssueEventEnvelope
        {
            EventId = "evt-1",
            EventType = "ISSUE_CREATED",
            Payload = new IssueEventPayload
            {
                Id = "issue-2",
                Key = "PROJ-2",
                SprintId = "sprint-10",
                Status = "TODO",
                StoryPoints = 5
            }
        });

        // 2. Transition to DONE
        var statusChange = new IssueEventEnvelope
        {
            EventId = "evt-2",
            EventType = "ISSUE_STATUS_CHANGED",
            Payload = new IssueEventPayload
            {
                Id = "issue-2",
                Key = "PROJ-2",
                SprintId = "sprint-10",
                Status = "DONE",
                PreviousStatus = "IN_PROGRESS",
                StoryPoints = 5
            }
        };

        var result = await _sut.ProcessEnvelopeAsync(statusChange);
        result.Should().BeTrue();

        var snapshot = await _snapshotRepo.GetBySprintIdAsync("sprint-10");
        snapshot!.CompletedStoryPoints.Should().Be(5);
        snapshot.RemainingStoryPoints.Should().Be(0);
    }

    [Fact]
    public async Task ProcessEnvelope_ReopenedIssue_ReversesCompletedPoints()
    {
        // 1. Create issue with DONE
        await _sut.ProcessEnvelopeAsync(new IssueEventEnvelope
        {
            EventId = "evt-done-1",
            EventType = "ISSUE_CREATED",
            Payload = new IssueEventPayload
            {
                Id = "issue-done",
                SprintId = "sprint-reopen",
                Status = "DONE",
                StoryPoints = 10
            }
        });

        var snap1 = await _snapshotRepo.GetBySprintIdAsync("sprint-reopen");
        snap1!.CompletedStoryPoints.Should().Be(10);

        // 2. Reopen to IN_PROGRESS
        await _sut.ProcessEnvelopeAsync(new IssueEventEnvelope
        {
            EventId = "evt-reopen-2",
            EventType = "ISSUE_STATUS_CHANGED",
            Payload = new IssueEventPayload
            {
                Id = "issue-done",
                SprintId = "sprint-reopen",
                Status = "IN_PROGRESS",
                PreviousStatus = "DONE",
                StoryPoints = 10
            }
        });

        var snap2 = await _snapshotRepo.GetBySprintIdAsync("sprint-reopen");
        snap2!.CompletedStoryPoints.Should().Be(0);
        snap2.RemainingStoryPoints.Should().Be(10);
    }

    [Fact]
    public async Task ProcessEnvelope_T1_F21_03_IssueUpdated_AdjustsPointDeltas()
    {
        await _sut.ProcessEnvelopeAsync(new IssueEventEnvelope
        {
            EventId = "evt-upd-1",
            EventType = "ISSUE_CREATED",
            Payload = new IssueEventPayload
            {
                Id = "issue-upd",
                SprintId = "sprint-upd",
                Status = "TODO",
                StoryPoints = 5
            }
        });

        // Re-estimate from 5 to 13 points
        await _sut.ProcessEnvelopeAsync(new IssueEventEnvelope
        {
            EventId = "evt-upd-2",
            EventType = "ISSUE_UPDATED",
            Payload = new IssueEventPayload
            {
                Id = "issue-upd",
                SprintId = "sprint-upd",
                Status = "TODO",
                StoryPoints = 13
            }
        });

        var snapshot = await _snapshotRepo.GetBySprintIdAsync("sprint-upd");
        snapshot!.TotalStoryPoints.Should().Be(13);
        snapshot.RemainingStoryPoints.Should().Be(13);
    }

    [Fact]
    public async Task ProcessEnvelope_T1_F21_04_IssueDeleted_DecrementsPoints()
    {
        await _sut.ProcessEnvelopeAsync(new IssueEventEnvelope
        {
            EventId = "evt-del-1",
            EventType = "ISSUE_CREATED",
            Payload = new IssueEventPayload
            {
                Id = "issue-del",
                SprintId = "sprint-del",
                Status = "TODO",
                StoryPoints = 8
            }
        });

        await _sut.ProcessEnvelopeAsync(new IssueEventEnvelope
        {
            EventId = "evt-del-2",
            EventType = "ISSUE_DELETED",
            Payload = new IssueEventPayload
            {
                Id = "issue-del",
                SprintId = "sprint-del",
                Status = "TODO",
                StoryPoints = 8
            }
        });

        var snapshot = await _snapshotRepo.GetBySprintIdAsync("sprint-del");
        snapshot!.TotalStoryPoints.Should().Be(0);
        snapshot.RemainingStoryPoints.Should().Be(0);
        snapshot.TrackedIssues.Should().NotContainKey("issue-del");
    }

    [Fact]
    public async Task ProcessEnvelope_T2_F21_01_DuplicateEventId_IgnoredIdempotently()
    {
        var envelope = new IssueEventEnvelope
        {
            EventId = "evt-dup-unique",
            EventType = "ISSUE_CREATED",
            Payload = new IssueEventPayload
            {
                Id = "issue-dup",
                SprintId = "sprint-dup",
                Status = "TODO",
                StoryPoints = 10
            }
        };

        await _sut.ProcessEnvelopeAsync(envelope);
        // Second dispatch with same eventId
        await _sut.ProcessEnvelopeAsync(envelope);

        var snapshot = await _snapshotRepo.GetBySprintIdAsync("sprint-dup");
        snapshot!.TotalStoryPoints.Should().Be(10); // NOT 20
    }

    [Fact]
    public async Task ProcessEventJson_T2_F21_03_MalformedJson_ReturnsFalseGracefully()
    {
        var result = await _sut.ProcessEventJsonAsync("{ not valid json at all }");
        result.Should().BeFalse();

        var emptyResult = await _sut.ProcessEventJsonAsync("");
        emptyResult.Should().BeFalse();
    }

    [Fact]
    public async Task ProcessEnvelope_T2_F21_04_UnassignedSprint_IgnoredWithoutError()
    {
        var envelope = new IssueEventEnvelope
        {
            EventId = "evt-backlog-1",
            EventType = "ISSUE_CREATED",
            Payload = new IssueEventPayload
            {
                Id = "issue-backlog",
                SprintId = null,
                PreviousSprintId = null,
                StoryPoints = 5
            }
        };

        var result = await _sut.ProcessEnvelopeAsync(envelope);
        result.Should().BeTrue();
    }

    [Fact]
    public async Task ProcessEnvelope_ScopeCreep_TriggersAlertClient()
    {
        // 1. Create sprint with initial 10 points
        await _sut.ProcessEnvelopeAsync(new IssueEventEnvelope
        {
            EventId = "evt-sc-1",
            EventType = "ISSUE_CREATED",
            Payload = new IssueEventPayload
            {
                Id = "i-initial",
                SprintId = "sprint-sc",
                Status = "TODO",
                StoryPoints = 10
            }
        });

        // 2. Add 20 more points (Total 30, > 10 * 1.20 = 12)
        await _sut.ProcessEnvelopeAsync(new IssueEventEnvelope
        {
            EventId = "evt-sc-2",
            EventType = "ISSUE_CREATED",
            Payload = new IssueEventPayload
            {
                Id = "i-creep",
                SprintId = "sprint-sc",
                Status = "TODO",
                StoryPoints = 20
            }
        });

        // Assert: AlertService.TriggerSprintAlert was invoked with AlertType.ScopeCreep
        await _alertClient.Received().TriggerSprintAlertAsync(
            "sprint-sc",
            AlertType.ScopeCreep,
            Arg.Any<AlertSeverity>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }
}
