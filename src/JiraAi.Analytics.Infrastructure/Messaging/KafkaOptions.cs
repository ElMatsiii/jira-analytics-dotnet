namespace JiraAi.Analytics.Infrastructure.Messaging;

public class KafkaOptions
{
    public const string SectionName = "Kafka";
    public string BootstrapServers { get; set; } = "localhost:9092";
    public string IssuesTopic { get; set; } = "issues.events";
    public string QueryTopic { get; set; } = "analytics.query";
    public string ReplyTopic { get; set; } = "analytics.reply";
    public string IssuesGroupId { get; set; } = "jira-analytics-issues-consumer";
    public string QueryGroupId { get; set; } = "jira-analytics-query-consumer";
    public string AutoOffsetReset { get; set; } = "Earliest";
}
