namespace JiraAi.Analytics.Infrastructure.Persistence;

using System.Collections.Concurrent;
using JiraAi.Analytics.Domain.Models;
using JiraAi.Analytics.Domain.Repositories;

public class InMemorySnapshotRepository : ISnapshotRepository
{
    private readonly ConcurrentDictionary<string, SprintSnapshotDocument> _store = new();

    public Task<SprintSnapshotDocument?> GetBySprintIdAsync(string sprintId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sprintId))
        {
            return Task.FromResult<SprintSnapshotDocument?>(null);
        }

        _store.TryGetValue(sprintId, out var snapshot);
        return Task.FromResult(snapshot);
    }

    public Task UpsertSnapshotAsync(SprintSnapshotDocument snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot.UpdatedAt = DateTime.UtcNow;
        snapshot.Version++;
        _store[snapshot.SprintId] = snapshot;
        return Task.CompletedTask;
    }

    public Task AppendDailyBurndownAsync(string sprintId, DailyBurndownRecord burndownRecord, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(burndownRecord);

        if (_store.TryGetValue(sprintId, out var snapshot))
        {
            var idx = snapshot.DailyBurndown.FindIndex(d => d.Date == burndownRecord.Date);
            if (idx >= 0)
            {
                snapshot.DailyBurndown[idx] = burndownRecord;
            }
            else
            {
                snapshot.DailyBurndown.Add(burndownRecord);
            }
        }

        return Task.CompletedTask;
    }

    public Task<List<SprintSnapshotDocument>> GetActiveSnapshotsAsync(CancellationToken cancellationToken = default)
    {
        var list = _store.Values.Where(x => x.Status == "ACTIVE").ToList();
        return Task.FromResult(list);
    }
}

public class InMemoryVelocityRepository : IVelocityRepository
{
    private readonly List<VelocityRecordDocument> _store = new();
    private readonly object _lock = new();

    public Task RecordVelocityAsync(VelocityRecordDocument velocityRecord, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(velocityRecord);
        lock (_lock)
        {
            _store.RemoveAll(x => x.ProjectId == velocityRecord.ProjectId && x.SprintId == velocityRecord.SprintId);
            _store.Add(velocityRecord);
        }
        return Task.CompletedTask;
    }

    public Task<List<VelocityRecordDocument>> GetRecentCompletedSprintsAsync(string projectId, int limit = 3, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var query = string.IsNullOrWhiteSpace(projectId)
                ? _store
                : _store.Where(x => x.ProjectId == projectId);

            var result = query
                .OrderByDescending(x => x.CompletedDate)
                .Take(limit)
                .ToList();

            return Task.FromResult(result);
        }
    }
}

public class InMemoryAlertRepository : IAlertRepository
{
    private readonly List<SprintAlertDocument> _alerts = new();
    private readonly object _lock = new();

    public Task RecordAlertAsync(SprintAlertDocument alert, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alert);
        lock (_lock)
        {
            _alerts.Add(alert);
        }
        return Task.CompletedTask;
    }

    public Task<DateTime?> GetLastAlertTimeAsync(string sprintId, string alertType, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var latest = _alerts
                .Where(x => x.SprintId == sprintId && x.AlertType == alertType)
                .OrderByDescending(x => x.DispatchedAt)
                .FirstOrDefault();

            return Task.FromResult<DateTime?>(latest?.DispatchedAt);
        }
    }
}
