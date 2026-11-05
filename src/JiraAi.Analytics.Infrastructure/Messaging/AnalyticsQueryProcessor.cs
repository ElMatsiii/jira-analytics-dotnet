namespace JiraAi.Analytics.Infrastructure.Messaging;

using System.Text;
using System.Text.Json;
using JiraAi.Analytics.Contracts.Dtos;
using JiraAi.Analytics.Domain.Models;
using JiraAi.Analytics.Domain.Repositories;
using JiraAi.Analytics.Domain.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public interface IAnalyticsQueryProcessor
{
    Task<bool> ProcessQueryAsync(
        string queryJson,
        string? correlationIdHeader = null,
        CancellationToken cancellationToken = default);

    Task<AnalyticsReplyDto> ComputeReplyAsync(
        AnalyticsQueryDto query,
        string correlationId,
        CancellationToken cancellationToken = default);
}

public class AnalyticsQueryProcessor : IAnalyticsQueryProcessor
{
    private readonly ISnapshotRepository _snapshotRepository;
    private readonly IVelocityRepository _velocityRepository;
    private readonly IBurndownCalculator _burndownCalculator;
    private readonly IVelocityCalculator _velocityCalculator;
    private readonly IHealthScoreCalculator _healthScoreCalculator;
    private readonly IRiskDetectionEngine _riskEngine;
    private readonly IKafkaProducerWrapper _producer;
    private readonly KafkaOptions _kafkaOptions;
    private readonly ILogger<AnalyticsQueryProcessor> _logger;

    private static readonly HashSet<string> ValidQueryTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "BURNDOWN",
        "VELOCITY",
        "SPRINT_HEALTH",
        "FULL_DIAGNOSTIC",
        "BURNDOWN_SNAPSHOT",
        "VELOCITY_FORECAST",
        "SPRINT_HEALTH_AUDIT"
    };

    public AnalyticsQueryProcessor(
        ISnapshotRepository snapshotRepository,
        IVelocityRepository velocityRepository,
        IBurndownCalculator burndownCalculator,
        IVelocityCalculator velocityCalculator,
        IHealthScoreCalculator healthScoreCalculator,
        IRiskDetectionEngine riskEngine,
        IKafkaProducerWrapper producer,
        IOptions<KafkaOptions> kafkaOptions,
        ILogger<AnalyticsQueryProcessor> logger)
    {
        _snapshotRepository = snapshotRepository ?? throw new ArgumentNullException(nameof(snapshotRepository));
        _velocityRepository = velocityRepository ?? throw new ArgumentNullException(nameof(velocityRepository));
        _burndownCalculator = burndownCalculator ?? throw new ArgumentNullException(nameof(burndownCalculator));
        _velocityCalculator = velocityCalculator ?? throw new ArgumentNullException(nameof(velocityCalculator));
        _healthScoreCalculator = healthScoreCalculator ?? throw new ArgumentNullException(nameof(healthScoreCalculator));
        _riskEngine = riskEngine ?? throw new ArgumentNullException(nameof(riskEngine));
        _producer = producer ?? throw new ArgumentNullException(nameof(producer));
        _kafkaOptions = kafkaOptions?.Value ?? new KafkaOptions();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<bool> ProcessQueryAsync(
        string queryJson,
        string? correlationIdHeader = null,
        CancellationToken cancellationToken = default)
    {
        // T2-F22-04: Empty or null query payload does not throw NullReferenceException
        if (string.IsNullOrWhiteSpace(queryJson))
        {
            _logger.LogWarning("Received empty or null analytics query.");
            return false;
        }

        AnalyticsQueryDto? query;
        try
        {
            query = JsonSerializer.Deserialize<AnalyticsQueryDto>(queryJson);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to deserialize analytics.query payload: {Json}", queryJson);
            return false;
        }

        if (query == null)
        {
            _logger.LogWarning("Deserialized analytics query is null.");
            return false;
        }

        // T1-F22-02 & T2-F22-02: Read correlationId from header, fallback to body, fallback to generated UUID
        var correlationId = !string.IsNullOrWhiteSpace(correlationIdHeader)
            ? correlationIdHeader
            : (!string.IsNullOrWhiteSpace(query.CorrelationId)
                ? query.CorrelationId
                : Guid.NewGuid().ToString());

        var replyDto = await ComputeReplyAsync(query, correlationId, cancellationToken);
        var replyJson = JsonSerializer.Serialize(replyDto);
        var replyTopic = !string.IsNullOrWhiteSpace(query.ReplyTopic) ? query.ReplyTopic : _kafkaOptions.ReplyTopic;

        // T1-F22-04: Produce reply preserving kafka_correlationId in headers
        var headers = new Dictionary<string, byte[]>
        {
            { "kafka_correlationId", Encoding.UTF8.GetBytes(correlationId) }
        };

        // T2-F22-03: Retries inside producer or with Polly
        int attempts = 0;
        while (attempts < 3)
        {
            attempts++;
            try
            {
                await _producer.ProduceAsync(replyTopic, query.SprintId, replyJson, headers, cancellationToken);
                _logger.LogInformation(
                    "Published analytics reply for sprint {SprintId} (correlationId: {CorrelationId}) to topic {Topic}",
                    query.SprintId, correlationId, replyTopic);
                return true;
            }
            catch (Exception ex) when (attempts < 3)
            {
                _logger.LogWarning(ex, "Attempt {Attempt} to publish analytics.reply failed, retrying...", attempts);
                await Task.Delay(200 * attempts, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to publish analytics.reply after {Attempts} attempts.", attempts);
                throw;
            }
        }

        return false;
    }

    public async Task<AnalyticsReplyDto> ComputeReplyAsync(
        AnalyticsQueryDto query,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        bool isUnrecognizedType = !string.IsNullOrWhiteSpace(query.QueryType) &&
            !ValidQueryTypes.Contains(query.QueryType);

        var snapshot = await _snapshotRepository.GetBySprintIdAsync(query.SprintId, cancellationToken);

        // T2-F22-01: Query for sprint with no data returns valid reply with zeroed metrics and status SUCCESS
        bool isInitializing = false;
        if (snapshot == null)
        {
            isInitializing = true;
            snapshot = new SprintSnapshotDocument
            {
                SprintId = query.SprintId,
                ProjectId = query.ProjectId ?? string.Empty,
                SprintName = $"Sprint {query.SprintId}",
                StartDate = DateTime.UtcNow,
                EndDate = DateTime.UtcNow.AddDays(14),
                Status = "ACTIVE",
                InitialCommittedPoints = 0,
                TotalStoryPoints = 0,
                CompletedStoryPoints = 0,
                RemainingStoryPoints = 0
            };
        }

        var burndownData = _burndownCalculator.CalculateBurndownSeries(snapshot);
        var velocityHistory = await _velocityRepository.GetRecentCompletedSprintsAsync(
            snapshot.ProjectId, 3, cancellationToken);

        var velocity = _velocityCalculator.CalculateRollingVelocity(velocityHistory, snapshot.CompletedStoryPoints);
        var healthScore = _healthScoreCalculator.CalculateHealthScore(snapshot);
        var (diagnostics, recommendations) = _riskEngine.GenerateDiagnosticsAndRecommendations(snapshot);

        if (isInitializing)
        {
            diagnostics.Clear();
            diagnostics.Add("Sprint is INITIALIZING: No committed issues or snapshots have been recorded yet.");
            recommendations.Clear();
            recommendations.Add("Assign issues to sprint and establish initial story point commitment.");
        }

        // T2-F22-05: Unrecognized queryType returns diagnostic warning
        if (isUnrecognizedType)
        {
            diagnostics.Add($"Warning: Unrecognized queryType '{query.QueryType}'. Computed full standard analytics.");
        }

        return new AnalyticsReplyDto
        {
            ReplyId = Guid.NewGuid().ToString(),
            CorrelationId = correlationId,
            SprintId = query.SprintId,
            Status = "SUCCESS",
            ErrorMessage = null,
            TotalPoints = snapshot.TotalStoryPoints,
            CompletedPoints = snapshot.CompletedStoryPoints,
            RemainingPoints = snapshot.RemainingStoryPoints,
            Velocity = velocity,
            HealthScore = healthScore,
            BurndownData = burndownData,
            Diagnostics = diagnostics,
            Recommendations = recommendations,
            Timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
        };
    }
}
