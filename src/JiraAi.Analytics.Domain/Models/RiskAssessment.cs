namespace JiraAi.Analytics.Domain.Models;

/// <summary>Kind of sprint risk detected by MS3 (local model; mapped to MS2's AlertType at the gRPC edge).</summary>
public enum RiskType
{
    Unspecified = 0,
    ScopeCreep = 1,
    VelocityDeficit = 2,
    BottleneckDetected = 3
}

/// <summary>Severity of a detected risk (local model; mapped to MS2's AlertSeverity at the gRPC edge).</summary>
public enum RiskSeverity
{
    Unspecified = 0,
    Info = 1,
    Warning = 2,
    Critical = 3
}

public class RiskAssessment
{
    public RiskType Type { get; set; } = RiskType.Unspecified;

    public RiskSeverity Severity { get; set; } = RiskSeverity.Info;

    public string Title { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public string Recommendation { get; set; } = string.Empty;

    public double MetricValue { get; set; }
}
