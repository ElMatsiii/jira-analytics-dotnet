namespace JiraAi.Analytics.Domain.Services;

using JiraAi.Analytics.Contracts.Dtos;
using JiraAi.Analytics.Domain.Models;

public interface IBurndownCalculator
{
    List<BurndownPointDto> CalculateBurndownSeries(SprintSnapshotDocument snapshot, DateTime? asOfDate = null);
}

public class BurndownCalculator : IBurndownCalculator
{
    public List<BurndownPointDto> CalculateBurndownSeries(SprintSnapshotDocument snapshot, DateTime? asOfDate = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var points = new List<BurndownPointDto>();
        var startDate = snapshot.StartDate.Date;
        var endDate = snapshot.EndDate.Date;

        // T2-F19-02: Guard against duration <= 0 days (minimum 1 day)
        var totalDays = Math.Max(1, (endDate - startDate).Days);
        var totalPoints = Math.Max(0, snapshot.TotalStoryPoints);
        var idealDecrement = (double)totalPoints / totalDays;

        var currentDay = (asOfDate ?? DateTime.UtcNow).Date;
        var recordedByDate = snapshot.DailyBurndown
            .GroupBy(d => d.Date)
            .ToDictionary(g => g.Key, g => g.Last());

        for (int dayIndex = 0; dayIndex <= totalDays; dayIndex++)
        {
            var dayDate = startDate.AddDays(dayIndex);
            var dateStr = dayDate.ToString("yyyy-MM-dd");

            // T1-F19-01: Ideal burndown line
            var idealRemaining = Math.Max(0.0, Math.Round(totalPoints - (dayIndex * idealDecrement), 2));

            double actualRemaining;
            double? completedOnDay = null;

            if (recordedByDate.TryGetValue(dateStr, out var recorded))
            {
                // T2-F19-03: Negative completed points clamped to zero
                actualRemaining = Math.Max(0.0, recorded.ActualRemaining);
                completedOnDay = Math.Max(0.0, recorded.CompletedOnDay);
            }
            else
            {
                if (dayDate <= currentDay)
                {
                    actualRemaining = Math.Max(0.0, snapshot.RemainingStoryPoints);
                }
                else
                {
                    actualRemaining = Math.Max(0.0, snapshot.RemainingStoryPoints);
                }
            }

            points.Add(new BurndownPointDto
            {
                Date = dateStr,
                DayIndex = dayIndex,
                IdealRemaining = idealRemaining,
                ActualRemaining = actualRemaining,
                CompletedOnDay = completedOnDay
            });
        }

        return points;
    }
}
