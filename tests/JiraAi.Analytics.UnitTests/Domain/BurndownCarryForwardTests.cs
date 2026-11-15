namespace JiraAi.Analytics.UnitTests.Domain;

using FluentAssertions;
using JiraAi.Analytics.Domain.Models;
using JiraAi.Analytics.Domain.Services;
using Xunit;

/// <summary>Actual line: past days without events keep the last known value; future days are not drawn.</summary>
public class BurndownCarryForwardTests
{
    private readonly BurndownCalculator _sut = new();
    private static readonly DateTime Start = new(2026, 11, 16, 0, 0, 0, DateTimeKind.Utc);

    private static SprintSnapshotDocument Snapshot(int total, int remaining, params (int Day, double Actual, double Completed)[] records) => new()
    {
        SprintId = "s1",
        StartDate = Start,
        EndDate = Start.AddDays(10),
        TotalStoryPoints = total,
        RemainingStoryPoints = remaining,
        DailyBurndown = records.Select(r => new DailyBurndownRecord
        {
            Date = Start.AddDays(r.Day).ToString("yyyy-MM-dd"),
            DayIndex = r.Day,
            ActualRemaining = r.Actual,
            CompletedOnDay = r.Completed
        }).ToList()
    };

    [Fact]
    public void PastDaysWithoutEvents_KeepTheLastKnownValue()
    {
        // Events on day 0 (20 left) and day 3 (12 left); current remaining is 12. Today is day 6.
        var series = _sut.CalculateBurndownSeries(Snapshot(20, 12, (0, 20, 0), (3, 12, 8)), asOfDate: Start.AddDays(6));

        series.Take(7).Select(p => p.ActualRemaining).Should().Equal(20, 20, 20, 12, 12, 12, 12);
        series[1].CompletedOnDay.Should().BeNull("no event happened that day");
        series[3].CompletedOnDay.Should().Be(8);
    }

    [Fact]
    public void FutureDays_HaveNoActualValue_ButKeepTheIdealLine()
    {
        var series = _sut.CalculateBurndownSeries(Snapshot(20, 12, (0, 20, 0), (3, 12, 8)), asOfDate: Start.AddDays(6));

        series.Should().HaveCount(11);
        series.Skip(7).Should().OnlyContain(p => p.ActualRemaining == null);
        series.Select(p => p.IdealRemaining).Should().Equal(20, 18, 16, 14, 12, 10, 8, 6, 4, 2, 0);
    }

    [Fact]
    public void DaysBeforeTheFirstEvent_UseTheEarliestRecordedValue()
    {
        // Issues were added on day 2; days 0-1 show that first known scope instead of the current value.
        var series = _sut.CalculateBurndownSeries(Snapshot(15, 5, (2, 15, 0), (4, 5, 10)), asOfDate: Start.AddDays(4));

        series.Take(5).Select(p => p.ActualRemaining).Should().Equal(15, 15, 15, 15, 5);
    }

    [Fact]
    public void WithoutAnyRecord_PastDaysUseTheCurrentRemainingPoints()
    {
        var series = _sut.CalculateBurndownSeries(Snapshot(9, 9), asOfDate: Start.AddDays(2));

        series.Take(3).Select(p => p.ActualRemaining).Should().Equal(9, 9, 9);
        series.Skip(3).Should().OnlyContain(p => p.ActualRemaining == null);
    }

    [Fact]
    public void SprintNotStarted_HasNoActualPointsAtAll()
    {
        var series = _sut.CalculateBurndownSeries(Snapshot(10, 10), asOfDate: Start.AddDays(-3));

        series.Should().OnlyContain(p => p.ActualRemaining == null);
    }

    [Fact]
    public void FinishedSprint_DrawsEveryDay()
    {
        var series = _sut.CalculateBurndownSeries(Snapshot(10, 2, (0, 10, 0), (9, 2, 8)), asOfDate: Start.AddDays(30));

        series.Should().OnlyContain(p => p.ActualRemaining != null);
        series.Last().ActualRemaining.Should().Be(2, "day 10 carries day 9's value");
    }
}
