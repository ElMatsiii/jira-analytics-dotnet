namespace JiraAi.Analytics.Domain.Repositories;

using JiraAi.Analytics.Domain.Models;

public interface ISnapshotRepository
{
    Task<SprintSnapshotDocument?> GetBySprintIdAsync(string sprintId, CancellationToken cancellationToken = default);
    Task UpsertSnapshotAsync(SprintSnapshotDocument snapshot, CancellationToken cancellationToken = default);
    Task AppendDailyBurndownAsync(string sprintId, DailyBurndownRecord burndownRecord, CancellationToken cancellationToken = default);
    /// <summary>Snapshots whose sprint is not closed yet (status other than COMPLETED or CANCELLED).</summary>
    Task<List<SprintSnapshotDocument>> GetOpenSnapshotsAsync(CancellationToken cancellationToken = default);
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

/// <summary>
/// Read access to sprint data owned by MS2. The implementation calls MS2's SprintService over gRPC
/// and converts the answer to <see cref="SprintInfo"/>; it returns null when the sprint is unknown or
/// MS2 is unreachable.
/// </summary>
public interface ISprintDirectory
{
    Task<SprintInfo?> GetSprintAsync(string sprintId, CancellationToken cancellationToken = default);
}
