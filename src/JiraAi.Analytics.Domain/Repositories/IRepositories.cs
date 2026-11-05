namespace JiraAi.Analytics.Domain.Repositories;

using JiraAi.Analytics.Domain.Models;

public interface ISnapshotRepository
{
    Task<SprintSnapshotDocument?> GetBySprintIdAsync(string sprintId, CancellationToken cancellationToken = default);
    Task UpsertSnapshotAsync(SprintSnapshotDocument snapshot, CancellationToken cancellationToken = default);
    Task AppendDailyBurndownAsync(string sprintId, DailyBurndownRecord burndownRecord, CancellationToken cancellationToken = default);
    Task<List<SprintSnapshotDocument>> GetActiveSnapshotsAsync(CancellationToken cancellationToken = default);
}

public interface IVelocityRepository
{
    Task RecordVelocityAsync(VelocityRecordDocument velocityRecord, CancellationToken cancellationToken = default);
    Task<List<VelocityRecordDocument>> GetRecentCompletedSprintsAsync(string projectId, int limit = 3, CancellationToken cancellationToken = default);
}

public interface IAlertRepository
{
    Task RecordAlertAsync(SprintAlertDocument alert, CancellationToken cancellationToken = default);
    Task<DateTime?> GetLastAlertTimeAsync(string sprintId, string alertType, CancellationToken cancellationToken = default);
}
