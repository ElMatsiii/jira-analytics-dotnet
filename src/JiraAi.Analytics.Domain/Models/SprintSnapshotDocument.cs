namespace JiraAi.Analytics.Domain.Models;

using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

public class SprintSnapshotDocument
{
    [BsonId]
    [BsonRepresentation(BsonType.String)]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [BsonElement("sprintId")]
    public string SprintId { get; set; } = string.Empty;

    [BsonElement("projectId")]
    public string ProjectId { get; set; } = string.Empty;

    [BsonElement("sprintName")]
    public string SprintName { get; set; } = string.Empty;

    [BsonElement("startDate")]
    public DateTime StartDate { get; set; } = DateTime.UtcNow;

    [BsonElement("endDate")]
    public DateTime EndDate { get; set; } = DateTime.UtcNow.AddDays(14);

    [BsonElement("status")]
    public string Status { get; set; } = "ACTIVE"; // PLANNED, ACTIVE, COMPLETED

    [BsonElement("initialCommittedPoints")]
    public int InitialCommittedPoints { get; set; }

    [BsonElement("totalStoryPoints")]
    public int TotalStoryPoints { get; set; }

    [BsonElement("completedStoryPoints")]
    public int CompletedStoryPoints { get; set; }

    [BsonElement("remainingStoryPoints")]
    public int RemainingStoryPoints { get; set; }

    [BsonElement("dailyBurndown")]
    public List<DailyBurndownRecord> DailyBurndown { get; set; } = new();

    [BsonElement("trackedIssues")]
    public Dictionary<string, IssueTrackingRecord> TrackedIssues { get; set; } = new();

    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [BsonElement("updatedAt")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [BsonElement("version")]
    public long Version { get; set; } = 1;
}

public class DailyBurndownRecord
{
    [BsonElement("date")]
    public string Date { get; set; } = string.Empty; // Format: "yyyy-MM-dd"

    [BsonElement("dayIndex")]
    public int DayIndex { get; set; }

    [BsonElement("idealRemaining")]
    public double IdealRemaining { get; set; }

    [BsonElement("actualRemaining")]
    public double ActualRemaining { get; set; }

    [BsonElement("completedOnDay")]
    public double CompletedOnDay { get; set; }
}

public class IssueTrackingRecord
{
    [BsonElement("issueId")]
    public string IssueId { get; set; } = string.Empty;

    [BsonElement("key")]
    public string Key { get; set; } = string.Empty;

    [BsonElement("status")]
    public string Status { get; set; } = "TODO"; // TODO, IN_PROGRESS, IN_REVIEW, DONE

    [BsonElement("storyPoints")]
    public int StoryPoints { get; set; }

    [BsonElement("updatedAt")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
