namespace JiraAi.Analytics.Contracts.Dtos;

using System.Text.Json.Serialization;

public class IssueEventEnvelope
{
    [JsonPropertyName("eventId")]
    public string EventId { get; set; } = string.Empty;

    [JsonPropertyName("eventType")]
    public string EventType { get; set; } = string.Empty;

    [JsonPropertyName("aggregateId")]
    public string? AggregateId { get; set; }

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public int? Version { get; set; }

    [JsonPropertyName("payload")]
    public IssueEventPayload Payload { get; set; } = new();
}

public class IssueEventPayload
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("projectId")]
    public string ProjectId { get; set; } = string.Empty;

    [JsonPropertyName("sprintId")]
    public string? SprintId { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "TODO";

    [JsonPropertyName("priority")]
    public string Priority { get; set; } = "MEDIUM";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "STORY";

    [JsonPropertyName("storyPoints")]
    public int StoryPoints { get; set; }

    [JsonPropertyName("assigneeId")]
    public string? AssigneeId { get; set; }

    [JsonPropertyName("reporterId")]
    public string? ReporterId { get; set; }

    [JsonPropertyName("acceptanceCriteria")]
    public string? AcceptanceCriteria { get; set; }

    [JsonPropertyName("createdAt")]
    public string? CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public string? UpdatedAt { get; set; }

    [JsonPropertyName("previousStatus")]
    public string? PreviousStatus { get; set; }

    [JsonPropertyName("previousSprintId")]
    public string? PreviousSprintId { get; set; }
}
