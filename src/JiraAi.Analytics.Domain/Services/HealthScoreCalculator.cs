namespace JiraAi.Analytics.Domain.Services;

using JiraAi.Analytics.Domain.Models;

public interface IHealthScoreCalculator
{
    double CalculateHealthScore(SprintSnapshotDocument snapshot, DateTime? asOfDate = null);
}

public class HealthScoreCalculator : IHealthScoreCalculator
{
    public double CalculateHealthScore(SprintSnapshotDocument snapshot, DateTime? asOfDate = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // If sprint is completed and all points are done, full score
        if (snapshot.Status == "COMPLETED" || (snapshot.TotalStoryPoints > 0 && snapshot.RemainingStoryPoints == 0))
        {
            return 100.0;
        }

        if (snapshot.TotalStoryPoints == 0)
        {
            return 100.0;
        }

        var currentDate = (asOfDate ?? DateTime.UtcNow).Date;
        var startDate = snapshot.StartDate.Date;
        var endDate = snapshot.EndDate.Date;

        var totalDays = Math.Max(1, (endDate - startDate).Days);
        var elapsedDays = Math.Max(0, (currentDate - startDate).Days);
        var timeElapsedRatio = Math.Clamp((double)elapsedDays / totalDays, 0.0, 1.0);

        var totalPoints = Math.Max(1, snapshot.TotalStoryPoints);
        var completedPoints = Math.Max(0, snapshot.CompletedStoryPoints);
        var completionRatio = Math.Clamp((double)completedPoints / totalPoints, 0.0, 1.0);

        // Baseline score starts at 100, penalized by discrepancy between time elapsed and completion
        double paceGap = Math.Abs(timeElapsedRatio - completionRatio);
        double score = 100.0 - (paceGap * 50.0);

        // Penalty for scope creep (> 20% increase over initial commitment)
        if (snapshot.InitialCommittedPoints > 0 && snapshot.TotalStoryPoints > snapshot.InitialCommittedPoints * 1.20)
        {
            score -= 20.0;
        }

        // Penalty for bottleneck (high WIP in review/in_progress)
        if (snapshot.TrackedIssues.Count > 0 && snapshot.RemainingStoryPoints > 0)
        {
            int wipPoints = snapshot.TrackedIssues.Values
                .Where(i => i.Status is "IN_PROGRESS" or "IN_REVIEW")
                .Sum(i => i.StoryPoints);

            if ((double)wipPoints / snapshot.RemainingStoryPoints > 0.50)
            {
                score -= 10.0;
            }
        }

        // T1-F19-04 & Schema constraint: Bounded between 0.0 and 100.0
        return Math.Round(Math.Clamp(score, 0.0, 100.0), 1);
    }
}
