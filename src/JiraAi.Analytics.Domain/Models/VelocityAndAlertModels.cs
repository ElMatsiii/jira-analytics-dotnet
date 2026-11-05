namespace JiraAi.Analytics.Domain.Models;

using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

public class VelocityRecordDocument
{
    [BsonId]
    [BsonRepresentation(BsonType.String)]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [BsonElement("projectId")]
    public string ProjectId { get; set; } = string.Empty;

    [BsonElement("sprintId")]
    public string SprintId { get; set; } = string.Empty;

    [BsonElement("sprintName")]
    public string SprintName { get; set; } = string.Empty;

    [BsonElement("committedStoryPoints")]
    public int CommittedStoryPoints { get; set; }

    [BsonElement("completedStoryPoints")]
    public int CompletedStoryPoints { get; set; }

    [BsonElement("completedDate")]
    public DateTime CompletedDate { get; set; } = DateTime.UtcNow;
}

public class SprintAlertDocument
{
    [BsonId]
    [BsonRepresentation(BsonType.String)]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [BsonElement("sprintId")]
    public string SprintId { get; set; } = string.Empty;

    [BsonElement("alertType")]
    public string AlertType { get; set; } = string.Empty;

    [BsonElement("severity")]
    public string Severity { get; set; } = "ALERT_SEVERITY_WARNING";

    [BsonElement("message")]
    public string Message { get; set; } = string.Empty;

    [BsonElement("dispatchedAt")]
    public DateTime DispatchedAt { get; set; } = DateTime.UtcNow;

    [BsonElement("acknowledged")]
    public bool Acknowledged { get; set; } = false;
}

public record AlertResult(
    bool Success,
    bool Acknowledged,
    string? AlertId,
    bool Debounced,
    string? ErrorMessage)
{
    public static AlertResult Succeeded(string? alertId, bool acknowledged = true) =>
        new(Success: true, Acknowledged: acknowledged, AlertId: alertId, Debounced: false, ErrorMessage: null);

    public static AlertResult SuppressedByDebounce() =>
        new(Success: true, Acknowledged: false, AlertId: null, Debounced: true, ErrorMessage: null);

    public static AlertResult Failed(string errorMessage) =>
        new(Success: false, Acknowledged: false, AlertId: null, Debounced: false, ErrorMessage: errorMessage);
}
