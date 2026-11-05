namespace JiraAi.Analytics.Contracts.Dtos;

using System.Text.Json.Serialization;

public class AnalyticsQueryDto
{
    [JsonPropertyName("queryId")]
    public string QueryId { get; set; } = string.Empty;

    [JsonPropertyName("correlationId")]
    public string CorrelationId { get; set; } = string.Empty;

    [JsonPropertyName("replyTopic")]
    public string? ReplyTopic { get; set; } = "analytics.reply";

    [JsonPropertyName("sprintId")]
    public string SprintId { get; set; } = string.Empty;

    [JsonPropertyName("projectId")]
    public string? ProjectId { get; set; }

    [JsonPropertyName("queryType")]
    public string QueryType { get; set; } = "BURNDOWN";

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = string.Empty;

    [JsonPropertyName("metadata")]
    public Dictionary<string, object>? Metadata { get; set; }
}
