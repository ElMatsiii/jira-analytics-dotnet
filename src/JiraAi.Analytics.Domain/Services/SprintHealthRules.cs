namespace JiraAi.Analytics.Domain.Services;

using JiraAi.Analytics.Domain.Models;

/// <summary>Health classification of a sprint, owned by MS3 (sent as healthStatus in analytics.reply).</summary>
public enum SprintHealthStatus
{
    Healthy,
    AtRisk,
    Critical,
    /// <summary>The sprint is PLANNED or its start date has not arrived yet.</summary>
    NotStarted,
    /// <summary>The sprint has no story points, so a score says nothing about it.</summary>
    NoScope
}

public static class SprintHealthRules
{
    /// <summary>Score at or above which a sprint is HEALTHY.</summary>
    public const double HealthyMinScore = 75;

    /// <summary>Score at or above which (and below HEALTHY) a sprint is AT_RISK; below it, CRITICAL.</summary>
    public const double AtRiskMinScore = 50;

    /// <summary>
    /// Classifies a sprint. A sprint that has not started is NOT_STARTED even without points (its scope
    /// is still being planned); a started sprint without points is NO_SCOPE; otherwise the score decides.
    /// </summary>
    public static SprintHealthStatus Classify(SprintSnapshotDocument snapshot, double healthScore, DateTime? asOfDate = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var today = (asOfDate ?? DateTime.UtcNow).Date;
        if (snapshot.Status == SnapshotStatus.Planned || today < snapshot.StartDate.Date)
        {
            return SprintHealthStatus.NotStarted;
        }
        if (snapshot.TotalStoryPoints <= 0)
        {
            return SprintHealthStatus.NoScope;
        }
        if (healthScore >= HealthyMinScore)
        {
            return SprintHealthStatus.Healthy;
        }
        return healthScore >= AtRiskMinScore ? SprintHealthStatus.AtRisk : SprintHealthStatus.Critical;
    }

    /// <summary>Value used in analytics.reply (healthStatus enum of the contract).</summary>
    public static string ToWireValue(SprintHealthStatus status) => status switch
    {
        SprintHealthStatus.Healthy => "HEALTHY",
        SprintHealthStatus.AtRisk => "AT_RISK",
        SprintHealthStatus.Critical => "CRITICAL",
        SprintHealthStatus.NotStarted => "NOT_STARTED",
        SprintHealthStatus.NoScope => "NO_SCOPE",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };
}
