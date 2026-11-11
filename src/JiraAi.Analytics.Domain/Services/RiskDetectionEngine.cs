namespace JiraAi.Analytics.Domain.Services;

using System.Globalization;
using JiraAi.Analytics.Domain.Models;
using JiraAi.Contracts.Sprints.V1;

public interface IRiskDetectionEngine
{
    List<RiskAssessment> EvaluateRisks(SprintSnapshotDocument snapshot, DateTime? asOfDate = null);
    (List<string> Diagnostics, List<string> Recommendations) GenerateDiagnosticsAndRecommendations(SprintSnapshotDocument snapshot, DateTime? asOfDate = null);
}

public class RiskDetectionEngine : IRiskDetectionEngine
{
    public List<RiskAssessment> EvaluateRisks(SprintSnapshotDocument snapshot, DateTime? asOfDate = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var risks = new List<RiskAssessment>();

        // 1. Scope Creep Rule (> 20% over initial commitment) - T1-F19-05 & T1-F23-01
        if (snapshot.InitialCommittedPoints > 0 && snapshot.TotalStoryPoints > snapshot.InitialCommittedPoints * 1.20)
        {
            double increasePct = Math.Round(
                ((double)(snapshot.TotalStoryPoints - snapshot.InitialCommittedPoints) / snapshot.InitialCommittedPoints) * 100.0, 1);
            var formattedIncreasePct = increasePct.ToString("F1", CultureInfo.InvariantCulture);

            risks.Add(new RiskAssessment
            {
                Type = AlertType.ScopeCreep,
                Severity = increasePct >= 50.0 ? AlertSeverity.Critical : AlertSeverity.Warning,
                Title = "Scope Creep Detected",
                Message = $"Scope creep of {formattedIncreasePct}% detected (threshold: 20%). Current total: {snapshot.TotalStoryPoints} pts vs initial: {snapshot.InitialCommittedPoints} pts.",
                Recommendation = "Review newly added scope and consider moving low-priority stories to backlog.",
                MetricValue = increasePct
            });
        }

        // 2. Velocity Deficit Rule - T1-F23-02
        var currentDate = (asOfDate ?? DateTime.UtcNow).Date;
        var totalDays = Math.Max(1, (snapshot.EndDate.Date - snapshot.StartDate.Date).Days);
        var elapsedDays = Math.Max(0, (currentDate - snapshot.StartDate.Date).Days);
        double timeElapsedRatio = Math.Clamp((double)elapsedDays / totalDays, 0.0, 1.0);

        if (timeElapsedRatio >= 0.50 && snapshot.TotalStoryPoints > 0)
        {
            double idealRemaining = Math.Max(0.0, snapshot.TotalStoryPoints * (1.0 - timeElapsedRatio));
            if (snapshot.RemainingStoryPoints > idealRemaining * 1.30)
            {
                double deficitPct = Math.Round(
                    ((double)(snapshot.RemainingStoryPoints - idealRemaining) / Math.Max(1.0, idealRemaining)) * 100.0, 1);

                risks.Add(new RiskAssessment
                {
                    Type = AlertType.VelocityDeficit,
                    Severity = AlertSeverity.Warning,
                    Title = "Velocity Deficit Detected",
                    Message = $"Velocity deficit detected: {snapshot.RemainingStoryPoints} pts remaining vs {idealRemaining:F1} ideal remaining at {timeElapsedRatio * 100:F0}% sprint duration.",
                    Recommendation = "Re-estimate remaining tasks and discuss scope reduction during standup.",
                    MetricValue = deficitPct
                });
            }
        }

        // 3. Bottleneck Detected Rule (WIP > 50% in Review/Progress)
        if (snapshot.RemainingStoryPoints > 0 && snapshot.TrackedIssues.Count > 0)
        {
            int wipPoints = snapshot.TrackedIssues.Values
                .Where(i => i.Status is "IN_PROGRESS" or "IN_REVIEW")
                .Sum(i => i.StoryPoints);

            double wipRatio = (double)wipPoints / snapshot.RemainingStoryPoints;
            if (wipRatio > 0.50)
            {
                risks.Add(new RiskAssessment
                {
                    Type = AlertType.BottleneckDetected,
                    Severity = AlertSeverity.Info,
                    Title = "Bottleneck Detected",
                    Message = $"Bottleneck detected: {wipPoints} of {snapshot.RemainingStoryPoints} remaining pts ({wipRatio * 100:F0}%) are in progress or review.",
                    Recommendation = "Swarm on in-review and in-progress items to restore continuous flow.",
                    MetricValue = Math.Round(wipRatio * 100.0, 1)
                });
            }
        }

        return risks;
    }

    public (List<string> Diagnostics, List<string> Recommendations) GenerateDiagnosticsAndRecommendations(
        SprintSnapshotDocument snapshot, DateTime? asOfDate = null)
    {
        var risks = EvaluateRisks(snapshot, asOfDate);
        var diagnostics = risks.Select(r => r.Message).ToList();
        var recommendations = risks.Select(r => r.Recommendation).ToList();

        if (diagnostics.Count == 0)
        {
            diagnostics.Add("Sprint trajectory is on track with steady completion velocity.");
            recommendations.Add("Maintain current pace and continue daily standup synchronizations.");
        }

        return (diagnostics, recommendations);
    }
}
