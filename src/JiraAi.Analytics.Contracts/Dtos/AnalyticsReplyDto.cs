namespace JiraAi.Analytics.Contracts.Dtos;

using System.Text.Json.Serialization;

public class AnalyticsReplyDto
{
    [JsonPropertyName("replyId")]
    public string ReplyId { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("correlationId")]
    public string CorrelationId { get; set; } = string.Empty;

    [JsonPropertyName("sprintId")]
    public string SprintId { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = "SUCCESS";

    [JsonPropertyName("errorMessage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorMessage { get; set; }

    [JsonPropertyName("totalPoints")]
    public int TotalPoints { get; set; }

    [JsonPropertyName("completedPoints")]
    public int CompletedPoints { get; set; }

    [JsonPropertyName("remainingPoints")]
    public int RemainingPoints { get; set; }

    [JsonPropertyName("velocity")]
    public double Velocity { get; set; }

    [JsonPropertyName("healthScore")]
    public double HealthScore { get; set; }

    [JsonPropertyName("burndownData")]
    public List<BurndownPointDto> BurndownData { get; set; } = new();

    [JsonPropertyName("diagnostics")]
    public List<string> Diagnostics { get; set; } = new();

    [JsonPropertyName("recommendations")]
    public List<string> Recommendations { get; set; } = new();

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
}

public class BurndownPointDto
{
    [JsonPropertyName("date")]
    public string Date { get; set; } = string.Empty;

    [JsonPropertyName("dayIndex")]
    public int DayIndex { get; set; }

    [JsonPropertyName("idealRemaining")]
    public double IdealRemaining { get; set; }

    [JsonPropertyName("actualRemaining")]
    public double ActualRemaining { get; set; }

    [JsonPropertyName("completedOnDay")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? CompletedOnDay { get; set; }
}
