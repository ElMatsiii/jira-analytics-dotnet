namespace JiraAi.Analytics.Domain.Models;

using JiraAi.Contracts.Sprints.V1;

public class RiskAssessment
{
    public AlertType Type { get; set; } = AlertType.Unspecified;
    public AlertSeverity Severity { get; set; } = AlertSeverity.Info;
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Recommendation { get; set; } = string.Empty;
    public double MetricValue { get; set; }
}
