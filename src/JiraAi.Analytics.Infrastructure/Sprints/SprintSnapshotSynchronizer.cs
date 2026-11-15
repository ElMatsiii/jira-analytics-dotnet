namespace JiraAi.Analytics.Infrastructure.Sprints;

using JiraAi.Analytics.Domain.Models;
using JiraAi.Analytics.Domain.Repositories;
using JiraAi.Analytics.Domain.Services;
using Microsoft.Extensions.Logging;

public interface ISprintSnapshotSynchronizer
{
    /// <summary>New snapshot with MS2's real dates/name/status, or a provisional window if MS2 is unreachable.</summary>
    Task<SprintSnapshotDocument> CreateSnapshotAsync(string sprintId, string projectId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Refreshes dates/name/status from MS2. When MS2 reports the sprint COMPLETED, closes the snapshot
    /// and records its velocity (once). Returns true if the snapshot changed and must be persisted.
    /// </summary>
    Task<bool> SynchronizeAsync(SprintSnapshotDocument snapshot, CancellationToken cancellationToken = default);

    /// <summary>Synchronizes and persists every open snapshot; returns how many sprints were closed.</summary>
    Task<int> ReconcileOpenSnapshotsAsync(CancellationToken cancellationToken = default);
}

public class SprintSnapshotSynchronizer : ISprintSnapshotSynchronizer
{
    private readonly ISprintDirectory _sprints;
    private readonly ISnapshotRepository _snapshots;
    private readonly IVelocityRepository _velocity;
    private readonly ILogger<SprintSnapshotSynchronizer> _logger;
    private readonly Func<DateTime> _clock;

    public SprintSnapshotSynchronizer(
        ISprintDirectory sprints,
        ISnapshotRepository snapshots,
        IVelocityRepository velocity,
        ILogger<SprintSnapshotSynchronizer> logger,
        Func<DateTime>? clock = null)
    {
        _sprints = sprints ?? throw new ArgumentNullException(nameof(sprints));
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _velocity = velocity ?? throw new ArgumentNullException(nameof(velocity));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public async Task<SprintSnapshotDocument> CreateSnapshotAsync(string sprintId, string projectId, CancellationToken cancellationToken = default)
    {
        var info = await _sprints.GetSprintAsync(sprintId, cancellationToken);
        if (info == null)
        {
            _logger.LogWarning(
                "No sprint data from MS2 for {SprintId}; using a provisional {Days}-day window until it is available.",
                sprintId, SprintSnapshotRules.FallbackSprintDays);
        }
        var snapshot = SprintSnapshotRules.NewSnapshot(sprintId, projectId, info, _clock());
        if (info != null && SprintSnapshotRules.ShouldRecordVelocity(snapshot, info))
        {
            await CloseAsync(snapshot, info, cancellationToken);
        }
        return snapshot;
    }

    public async Task<bool> SynchronizeAsync(SprintSnapshotDocument snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var info = await _sprints.GetSprintAsync(snapshot.SprintId, cancellationToken);
        if (info == null)
        {
            return false; // keep what we have; MS2 may be temporarily unavailable
        }

        var changed = SprintSnapshotRules.ApplySprintInfo(snapshot, info);
        if (SprintSnapshotRules.ShouldRecordVelocity(snapshot, info))
        {
            await CloseAsync(snapshot, info, cancellationToken);
            changed = true;
        }
        else if (info.Status == SprintLifecycleStatus.Cancelled && snapshot.Status != SnapshotStatus.Cancelled)
        {
            snapshot.Status = SnapshotStatus.Cancelled; // a cancelled sprint does not count for velocity
            changed = true;
        }
        return changed;
    }

    public async Task<int> ReconcileOpenSnapshotsAsync(CancellationToken cancellationToken = default)
    {
        var closed = 0;
        foreach (var snapshot in await _snapshots.GetOpenSnapshotsAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await SynchronizeAsync(snapshot, cancellationToken))
                {
                    await _snapshots.UpsertSnapshotAsync(snapshot, cancellationToken);
                    if (snapshot.Status == SnapshotStatus.Completed)
                    {
                        closed++;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not reconcile snapshot of sprint {SprintId}", snapshot.SprintId);
            }
        }
        return closed;
    }

    private async Task CloseAsync(SprintSnapshotDocument snapshot, SprintInfo info, CancellationToken cancellationToken)
    {
        var record = SprintSnapshotRules.CloseAndBuildVelocityRecord(snapshot, info, _clock());
        // Upsert keyed by (projectId, sprintId): recording twice does not duplicate history.
        await _velocity.RecordVelocityAsync(record, cancellationToken);
        _logger.LogInformation(
            "Sprint {SprintId} completed in MS2: recorded velocity {Completed}/{Committed} SP for project {ProjectId}.",
            snapshot.SprintId, record.CompletedStoryPoints, record.CommittedStoryPoints, record.ProjectId);
    }
}
