namespace JiraAi.Analytics.Domain.Services;

using JiraAi.Analytics.Domain.Models;

public interface IBurndownCalculator
{
    List<BurndownPoint> CalculateBurndownSeries(SprintSnapshotDocument snapshot, DateTime? asOfDate = null);
}

public class BurndownCalculator : IBurndownCalculator
{
    public List<BurndownPoint> CalculateBurndownSeries(SprintSnapshotDocument snapshot, DateTime? asOfDate = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var points = new List<BurndownPoint>();
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

        // Value shown for days before the first recorded day: the earliest record if any (scope known
        // at that point), otherwise the current remaining points.
        var earliestRecord = snapshot.DailyBurndown
            .Where(d => !string.IsNullOrEmpty(d.Date))
            .OrderBy(d => d.Date, StringComparer.Ordinal)
            .FirstOrDefault();
        double? lastKnown = null;

        for (int dayIndex = 0; dayIndex <= totalDays; dayIndex++)
        {
            var dayDate = startDate.AddDays(dayIndex);
            var dateStr = dayDate.ToString("yyyy-MM-dd");

            // T1-F19-01: Ideal burndown line
            var idealRemaining = Math.Max(0.0, Math.Round(totalPoints - (dayIndex * idealDecrement), 2));

            double? actualRemaining;
            double? completedOnDay = null;

            if (dayDate > currentDay)
            {
                // A day that has not happened yet has no actual value: it is not drawn.
                actualRemaining = null;
            }
            else if (recordedByDate.TryGetValue(dateStr, out var recorded))
            {
                // T2-F19-03: Negative completed points clamped to zero
                actualRemaining = Math.Max(0.0, recorded.ActualRemaining);
                completedOnDay = Math.Max(0.0, recorded.CompletedOnDay);
            }
            else
            {
                // A past day without events keeps the last known value (nothing changed that day).
                actualRemaining = lastKnown
                    ?? Math.Max(0.0, earliestRecord?.ActualRemaining ?? snapshot.RemainingStoryPoints);
            }

            if (actualRemaining.HasValue)
            {
                lastKnown = actualRemaining;
            }

            points.Add(new BurndownPoint(dateStr, dayIndex, idealRemaining, actualRemaining, completedOnDay));
        }

        return points;
    }
}
