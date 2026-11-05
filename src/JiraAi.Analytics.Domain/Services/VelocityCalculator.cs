namespace JiraAi.Analytics.Domain.Services;

using JiraAi.Analytics.Domain.Models;

public interface IVelocityCalculator
{
    double CalculateRollingVelocity(IEnumerable<VelocityRecordDocument>? history, int currentCompletedPoints = 0);
    double CalculateSprintCompletionRate(int completedPoints, int totalPoints);
}

public class VelocityCalculator : IVelocityCalculator
{
    public double CalculateRollingVelocity(IEnumerable<VelocityRecordDocument>? history, int currentCompletedPoints = 0)
    {
        if (history == null)
        {
            return Math.Max(0.0, currentCompletedPoints);
        }

        var recentList = history
            .OrderByDescending(x => x.CompletedDate)
            .Take(3)
            .ToList();

        if (recentList.Count == 0)
        {
            return Math.Max(0.0, currentCompletedPoints);
        }

        // T1-F19-03 & T2-F24-04: Average completed points over last 1..3 sprints
        double totalCompleted = recentList.Sum(s => (double)Math.Max(0, s.CompletedStoryPoints));
        return Math.Round(totalCompleted / recentList.Count, 2);
    }

    public double CalculateSprintCompletionRate(int completedPoints, int totalPoints)
    {
        // T2-F19-01: Zero total story points computes 0% velocity without division by zero
        if (totalPoints <= 0)
        {
            return 0.0;
        }

        // T2-F19-04: Over-achievement can safely exceed 100%
        return Math.Round(((double)completedPoints / totalPoints) * 100.0, 2);
    }
}
