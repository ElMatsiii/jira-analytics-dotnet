namespace JiraAi.Analytics.Domain.Models;

/// <summary>Lifecycle of a sprint as owned by MS2 (local model).</summary>
public enum SprintLifecycleStatus
{
    Unknown = 0,
    Planned = 1,
    Active = 2,
    Completed = 3,
    Cancelled = 4
}

/// <summary>
/// Sprint data MS3 needs from MS2 (dates, name, status), converted from MS2's gRPC Sprint message
/// to MS3's own model (interoperability rule). Dates are UTC.
/// </summary>
public sealed record SprintInfo(
    string SprintId,
    string ProjectId,
    string Name,
    DateTime StartDate,
    DateTime EndDate,
    SprintLifecycleStatus Status)
{
    /// <summary>Status as stored in <see cref="SprintSnapshotDocument.Status"/>.</summary>
    public string StatusName => Status switch
    {
        SprintLifecycleStatus.Planned => SnapshotStatus.Planned,
        SprintLifecycleStatus.Active => SnapshotStatus.Active,
        SprintLifecycleStatus.Completed => SnapshotStatus.Completed,
        SprintLifecycleStatus.Cancelled => SnapshotStatus.Cancelled,
        _ => SnapshotStatus.Active
    };
}

/// <summary>Values of <see cref="SprintSnapshotDocument.Status"/>.</summary>
public static class SnapshotStatus
{
    public const string Planned = "PLANNED";
    public const string Active = "ACTIVE";
    public const string Completed = "COMPLETED";
    public const string Cancelled = "CANCELLED";

    public static bool IsClosed(string? status) => status == Completed || status == Cancelled;
}
