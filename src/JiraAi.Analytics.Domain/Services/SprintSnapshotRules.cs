namespace JiraAi.Analytics.Domain.Services;

using JiraAi.Analytics.Domain.Models;

/// <summary>Pure rules that keep a sprint snapshot aligned with the sprint data owned by MS2.</summary>
public static class SprintSnapshotRules
{
    /// <summary>Duration (in days) used when MS2 is unreachable and no real dates are known.</summary>
    public const int FallbackSprintDays = 14;

    /// <summary>New snapshot for a sprint, with real dates when MS2 answered, otherwise a provisional window.</summary>
    public static SprintSnapshotDocument NewSnapshot(string sprintId, string projectId, SprintInfo? info, DateTime now)
    {
        var snapshot = new SprintSnapshotDocument
        {
            SprintId = sprintId,
            ProjectId = projectId ?? string.Empty,
            SprintName = $"Sprint {sprintId}",
            StartDate = now,
            EndDate = now.AddDays(FallbackSprintDays),
            Status = SnapshotStatus.Active,
            InitialCommittedPoints = 0,
            TotalStoryPoints = 0,
            CompletedStoryPoints = 0,
            RemainingStoryPoints = 0
        };
        if (info != null)
        {
            ApplySprintInfo(snapshot, info);
        }
        return snapshot;
    }

    /// <summary>
    /// Copies name, dates, project and status from MS2 into the snapshot. A closed snapshot keeps its
    /// status (closing is handled by <see cref="ShouldRecordVelocity"/>). Returns true if anything changed.
    /// </summary>
    public static bool ApplySprintInfo(SprintSnapshotDocument snapshot, SprintInfo info)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(info);

        var changed = false;
        if (!string.IsNullOrWhiteSpace(info.Name) && snapshot.SprintName != info.Name)
        {
            snapshot.SprintName = info.Name;
            changed = true;
        }
        if (!string.IsNullOrWhiteSpace(info.ProjectId) && snapshot.ProjectId != info.ProjectId)
        {
            snapshot.ProjectId = info.ProjectId;
            changed = true;
        }
        if (snapshot.StartDate != info.StartDate)
        {
            snapshot.StartDate = info.StartDate;
            changed = true;
        }
        if (snapshot.EndDate != info.EndDate)
        {
            snapshot.EndDate = info.EndDate;
            changed = true;
        }
        if (!SnapshotStatus.IsClosed(snapshot.Status) && info.Status != SprintLifecycleStatus.Completed
            && snapshot.Status != info.StatusName)
        {
            snapshot.Status = info.StatusName;
            changed = true;
        }
        return changed;
    }

    /// <summary>True when MS2 reports the sprint completed and MS3 has not closed its snapshot yet.</summary>
    public static bool ShouldRecordVelocity(SprintSnapshotDocument snapshot, SprintInfo info) =>
        info.Status == SprintLifecycleStatus.Completed && snapshot.Status != SnapshotStatus.Completed;

    /// <summary>Closes the snapshot and builds the velocity record of the finished sprint.</summary>
    public static VelocityRecordDocument CloseAndBuildVelocityRecord(SprintSnapshotDocument snapshot, SprintInfo info, DateTime now)
    {
        snapshot.Status = SnapshotStatus.Completed;
        snapshot.UpdatedAt = now;
        return new VelocityRecordDocument
        {
            ProjectId = snapshot.ProjectId,
            SprintId = snapshot.SprintId,
            SprintName = snapshot.SprintName,
            CommittedStoryPoints = Math.Max(0, snapshot.TotalStoryPoints),
            CompletedStoryPoints = Math.Max(0, snapshot.CompletedStoryPoints),
            // The sprint's planned end orders history; fall back to now if MS2 sent no end date.
            CompletedDate = info.EndDate > DateTime.MinValue ? info.EndDate : now
        };
    }

    /// <summary>Days between start and end (minimum 1), used for the ideal burndown line.</summary>
    public static int DurationDays(SprintSnapshotDocument snapshot) =>
        Math.Max(1, (snapshot.EndDate.Date - snapshot.StartDate.Date).Days);
}
