namespace JiraAi.Analytics.Infrastructure.Messaging;

using System.Text.Json;
using JiraAi.Analytics.Contracts.Dtos;
using JiraAi.Analytics.Domain.Models;
using JiraAi.Analytics.Domain.Repositories;
using JiraAi.Analytics.Domain.Services;
using JiraAi.Analytics.Infrastructure.Grpc;
using JiraAi.Contracts.Sprints.V1;
using Microsoft.Extensions.Logging;

public interface IIssuesEventProcessor
{
    Task<bool> ProcessEventJsonAsync(string eventJson, CancellationToken cancellationToken = default);
    Task<bool> ProcessEnvelopeAsync(IssueEventEnvelope envelope, CancellationToken cancellationToken = default);
}

public class IssuesEventProcessor : IIssuesEventProcessor
{
    private readonly ISnapshotRepository _snapshotRepository;
    private readonly IRiskDetectionEngine _riskEngine;
    private readonly IAlertClientService _alertClient;
    private readonly ILogger<IssuesEventProcessor> _logger;
    private readonly HashSet<string> _processedEventIds = new();
    private readonly object _lock = new();

    public IssuesEventProcessor(
        ISnapshotRepository snapshotRepository,
        IRiskDetectionEngine riskEngine,
        IAlertClientService alertClient,
        ILogger<IssuesEventProcessor> logger)
    {
        _snapshotRepository = snapshotRepository ?? throw new ArgumentNullException(nameof(snapshotRepository));
        _riskEngine = riskEngine ?? throw new ArgumentNullException(nameof(riskEngine));
        _alertClient = alertClient ?? throw new ArgumentNullException(nameof(alertClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<bool> ProcessEventJsonAsync(string eventJson, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(eventJson))
        {
            _logger.LogWarning("Received empty or null issues event JSON.");
            return false;
        }

        try
        {
            var envelope = JsonSerializer.Deserialize<IssueEventEnvelope>(eventJson);
            if (envelope == null || string.IsNullOrWhiteSpace(envelope.EventId) || envelope.Payload == null)
            {
                _logger.LogWarning("Malformed issue event envelope received: {Json}", eventJson);
                return false; // T2-F21-03: Malformed payload handled safely without crashing
            }

            return await ProcessEnvelopeAsync(envelope, cancellationToken);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to deserialize issues event JSON: {Json}", eventJson);
            return false; // T2-F21-03
        }
    }

    public async Task<bool> ProcessEnvelopeAsync(IssueEventEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var payload = envelope.Payload;

        // T2-F21-01: Idempotency deduplication check on EventId
        lock (_lock)
        {
            if (!_processedEventIds.Add(envelope.EventId))
            {
                _logger.LogInformation("Duplicate eventId {EventId} ignored idempotently.", envelope.EventId);
                return true;
            }

            // Cap in-memory set size to prevent unbounded memory growth
            if (_processedEventIds.Count > 10000)
            {
                _processedEventIds.Clear();
                _processedEventIds.Add(envelope.EventId);
            }
        }

        // T2-F21-04: Unassigned sprint handling
        if (string.IsNullOrWhiteSpace(payload.SprintId) && string.IsNullOrWhiteSpace(payload.PreviousSprintId))
        {
            _logger.LogDebug("Issue {Key} is in backlog (unassigned to any sprint). Snapshot update skipped.", payload.Key);
            return true;
        }

        // Handle moving from a previous sprint to a new sprint or backlog
        if (!string.IsNullOrWhiteSpace(payload.PreviousSprintId) && payload.PreviousSprintId != payload.SprintId)
        {
            await RemoveIssueFromSprintAsync(payload.PreviousSprintId, payload, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(payload.SprintId))
        {
            return true;
        }

        // 1. Fetch or initialize snapshot
        var snapshot = await _snapshotRepository.GetBySprintIdAsync(payload.SprintId, cancellationToken);
        if (snapshot == null)
        {
            snapshot = new SprintSnapshotDocument
            {
                SprintId = payload.SprintId,
                ProjectId = payload.ProjectId,
                SprintName = $"Sprint {payload.SprintId}",
                StartDate = DateTime.UtcNow,
                EndDate = DateTime.UtcNow.AddDays(14),
                Status = "ACTIVE",
                InitialCommittedPoints = Math.Max(0, payload.StoryPoints),
                TotalStoryPoints = 0,
                CompletedStoryPoints = 0,
                RemainingStoryPoints = 0
            };
        }

        // 2. Process event type mutation
        switch (envelope.EventType)
        {
            case "ISSUE_CREATED":
            case "ISSUE_SPRINT_ASSIGNED":
                HandleIssueCreatedOrAssigned(snapshot, payload);
                break;

            case "ISSUE_STATUS_CHANGED":
                HandleIssueStatusChanged(snapshot, payload);
                break;

            case "ISSUE_UPDATED":
                HandleIssueUpdated(snapshot, payload);
                break;

            case "ISSUE_DELETED":
                HandleIssueDeleted(snapshot, payload);
                break;

            default:
                _logger.LogWarning("Unrecognized event type {EventType} for issue {Key}", envelope.EventType, payload.Key);
                HandleIssueUpdated(snapshot, payload);
                break;
        }

        // 3. Update daily burndown record for today
        var todayStr = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var dayIndex = Math.Max(0, (DateTime.UtcNow.Date - snapshot.StartDate.Date).Days);
        var existingDaily = snapshot.DailyBurndown.FirstOrDefault(d => d.Date == todayStr);
        if (existingDaily != null)
        {
            existingDaily.ActualRemaining = snapshot.RemainingStoryPoints;
            existingDaily.CompletedOnDay = snapshot.CompletedStoryPoints;
        }
        else
        {
            snapshot.DailyBurndown.Add(new DailyBurndownRecord
            {
                Date = todayStr,
                DayIndex = dayIndex,
                IdealRemaining = Math.Max(0.0, snapshot.TotalStoryPoints - (dayIndex * (snapshot.TotalStoryPoints / 14.0))),
                ActualRemaining = snapshot.RemainingStoryPoints,
                CompletedOnDay = snapshot.CompletedStoryPoints
            });
        }

        // 4. Persist snapshot (T1-F21-05)
        await _snapshotRepository.UpsertSnapshotAsync(snapshot, cancellationToken);

        // 5. Evaluate Risks & Dispatch Alerts (T1-F23-01..T1-F23-05)
        await EvaluateAndDispatchAlertsAsync(snapshot, cancellationToken);

        return true;
    }

    private void HandleIssueCreatedOrAssigned(SprintSnapshotDocument snapshot, IssueEventPayload payload)
    {
        if (snapshot.TrackedIssues.TryGetValue(payload.Id, out var existing))
        {
            // Already tracked; do not double count
            existing.Status = payload.Status;
            existing.StoryPoints = payload.StoryPoints;
            existing.UpdatedAt = DateTime.UtcNow;
            return;
        }

        var isDone = payload.Status == "DONE";
        var points = Math.Max(0, payload.StoryPoints);

        snapshot.TrackedIssues[payload.Id] = new IssueTrackingRecord
        {
            IssueId = payload.Id,
            Key = payload.Key,
            Status = payload.Status,
            StoryPoints = points,
            UpdatedAt = DateTime.UtcNow
        };

        snapshot.TotalStoryPoints += points;
        if (isDone)
        {
            snapshot.CompletedStoryPoints += points;
        }
        else
        {
            snapshot.RemainingStoryPoints += points;
        }

        if (snapshot.InitialCommittedPoints == 0)
        {
            snapshot.InitialCommittedPoints = snapshot.TotalStoryPoints;
        }
    }

    private void HandleIssueStatusChanged(SprintSnapshotDocument snapshot, IssueEventPayload payload)
    {
        string oldStatus = "TODO";
        int points = Math.Max(0, payload.StoryPoints);

        if (snapshot.TrackedIssues.TryGetValue(payload.Id, out var existing))
        {
            oldStatus = existing.Status;
            if (points == 0 && existing.StoryPoints > 0)
            {
                points = existing.StoryPoints;
            }
            existing.Status = payload.Status;
            existing.StoryPoints = points;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            oldStatus = payload.PreviousStatus ?? "TODO";
            snapshot.TrackedIssues[payload.Id] = new IssueTrackingRecord
            {
                IssueId = payload.Id,
                Key = payload.Key,
                Status = payload.Status,
                StoryPoints = points,
                UpdatedAt = DateTime.UtcNow
            };
            snapshot.TotalStoryPoints += points;
            snapshot.RemainingStoryPoints += points;
        }

        bool wasDone = oldStatus == "DONE";
        bool isDone = payload.Status == "DONE";

        // T1-F21-02: Transition to DONE updates completed points
        if (!wasDone && isDone)
        {
            snapshot.CompletedStoryPoints += points;
            snapshot.RemainingStoryPoints = Math.Max(0, snapshot.RemainingStoryPoints - points);
        }
        else if (wasDone && !isDone) // Reopened issue
        {
            snapshot.CompletedStoryPoints = Math.Max(0, snapshot.CompletedStoryPoints - points);
            snapshot.RemainingStoryPoints += points;
        }
    }

    private void HandleIssueUpdated(SprintSnapshotDocument snapshot, IssueEventPayload payload)
    {
        // T1-F21-03: Adjust story point estimates
        int oldPoints = 0;
        string currentStatus = payload.Status;

        if (snapshot.TrackedIssues.TryGetValue(payload.Id, out var existing))
        {
            oldPoints = existing.StoryPoints;
            currentStatus = payload.Status ?? existing.Status;
            existing.StoryPoints = Math.Max(0, payload.StoryPoints);
            existing.Status = currentStatus;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            snapshot.TrackedIssues[payload.Id] = new IssueTrackingRecord
            {
                IssueId = payload.Id,
                Key = payload.Key,
                Status = payload.Status,
                StoryPoints = Math.Max(0, payload.StoryPoints),
                UpdatedAt = DateTime.UtcNow
            };
        }

        int newPoints = Math.Max(0, payload.StoryPoints);
        int delta = newPoints - oldPoints;

        if (delta != 0)
        {
            snapshot.TotalStoryPoints = Math.Max(0, snapshot.TotalStoryPoints + delta);
            if (currentStatus == "DONE")
            {
                snapshot.CompletedStoryPoints = Math.Max(0, snapshot.CompletedStoryPoints + delta);
            }
            else
            {
                snapshot.RemainingStoryPoints = Math.Max(0, snapshot.RemainingStoryPoints + delta);
            }
        }
    }

    private void HandleIssueDeleted(SprintSnapshotDocument snapshot, IssueEventPayload payload)
    {
        // T1-F21-04: ISSUE_DELETED decrements points
        int points = Math.Max(0, payload.StoryPoints);
        string status = payload.Status;

        if (snapshot.TrackedIssues.TryGetValue(payload.Id, out var existing))
        {
            points = existing.StoryPoints;
            status = existing.Status;
            snapshot.TrackedIssues.Remove(payload.Id);
        }

        snapshot.TotalStoryPoints = Math.Max(0, snapshot.TotalStoryPoints - points);
        if (status == "DONE")
        {
            snapshot.CompletedStoryPoints = Math.Max(0, snapshot.CompletedStoryPoints - points);
        }
        else
        {
            snapshot.RemainingStoryPoints = Math.Max(0, snapshot.RemainingStoryPoints - points);
        }
    }

    private async Task RemoveIssueFromSprintAsync(string sprintId, IssueEventPayload payload, CancellationToken cancellationToken)
    {
        var prevSnapshot = await _snapshotRepository.GetBySprintIdAsync(sprintId, cancellationToken);
        if (prevSnapshot != null)
        {
            HandleIssueDeleted(prevSnapshot, payload);
            await _snapshotRepository.UpsertSnapshotAsync(prevSnapshot, cancellationToken);
        }
    }

    private async Task EvaluateAndDispatchAlertsAsync(SprintSnapshotDocument snapshot, CancellationToken cancellationToken)
    {
        var risks = _riskEngine.EvaluateRisks(snapshot);
        foreach (var risk in risks)
        {
            try
            {
                var result = await _alertClient.TriggerSprintAlertAsync(
                    snapshot.SprintId,
                    risk.Type,
                    risk.Severity,
                    risk.Message,
                    risk.Recommendation,
                    cancellationToken);

                if (!result.Success && !result.Debounced)
                {
                    _logger.LogWarning("Alert for sprint {SprintId} ({Type}) not dispatched: {Error}",
                        snapshot.SprintId, risk.Type, result.ErrorMessage);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to dispatch alert for sprint {SprintId}", snapshot.SprintId);
            }
        }
    }
}
