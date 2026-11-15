namespace JiraAi.Analytics.Domain.Models;

/// <summary>One day of a sprint burndown (local model; mapped to analytics.reply's burndownData item).</summary>
public sealed record BurndownPoint(
    string Date,
    int DayIndex,
    double IdealRemaining,
    double ActualRemaining,
    double? CompletedOnDay);
