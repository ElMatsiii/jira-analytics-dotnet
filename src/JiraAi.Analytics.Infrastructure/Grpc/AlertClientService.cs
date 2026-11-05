namespace JiraAi.Analytics.Infrastructure.Grpc;

using System.Collections.Concurrent;
using global::Grpc.Core;
using JiraAi.Analytics.Domain.Models;
using JiraAi.Analytics.Domain.Repositories;
using JiraAi.Contracts.Sprints.V1;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;

public class AlertClientOptions
{
    public const string SectionName = "AlertService";
    public string Endpoint { get; set; } = "http://localhost:50052";
    public int TimeoutMs { get; set; } = 2000; // T2-F23-03
    public int DebounceSeconds { get; set; } = 60; // T2-F23-02
    public int MaxRetries { get; set; } = 3;
}

public interface IAlertClientService
{
    Task<AlertResult> TriggerSprintAlertAsync(
        string sprintId,
        AlertType type,
        AlertSeverity severity,
        string message,
        string recommendation,
        CancellationToken cancellationToken = default);
}

public class AlertClientService : IAlertClientService
{
    private readonly AlertService.AlertServiceClient _client;
    private readonly IAlertRepository? _alertRepository;
    private readonly AlertClientOptions _options;
    private readonly ILogger<AlertClientService> _logger;
    private readonly ConcurrentDictionary<(string SprintId, AlertType Type), DateTime> _lastAlertDispatches = new();
    private readonly AsyncRetryPolicy _retryPolicy;

    public AlertClientService(
        AlertService.AlertServiceClient client,
        IOptions<AlertClientOptions> options,
        ILogger<AlertClientService> logger,
        IAlertRepository? alertRepository = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _options = options?.Value ?? new AlertClientOptions();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _alertRepository = alertRepository;

        // Polly retry policy for transient gRPC errors
        _retryPolicy = Policy
            .Handle<RpcException>(ex =>
                ex.StatusCode == StatusCode.Unavailable ||
                ex.StatusCode == StatusCode.DeadlineExceeded)
            .WaitAndRetryAsync(
                _options.MaxRetries,
                retryAttempt => TimeSpan.FromMilliseconds(150 * Math.Pow(2, retryAttempt - 1)),
                (outcome, timespan, retryCount, context) =>
                {
                    _logger.LogWarning(
                        "MS2 AlertService call failed transiently: {Message}. Retry {RetryCount} in {DelayMs}ms",
                        outcome.Message,
                        retryCount,
                        timespan.TotalMilliseconds);
                });
    }

    public async Task<AlertResult> TriggerSprintAlertAsync(
        string sprintId,
        AlertType type,
        AlertSeverity severity,
        string message,
        string recommendation,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sprintId))
        {
            throw new ArgumentException("SprintId is required for alert dispatch.", nameof(sprintId));
        }

        // 1. Debounce Check: T2-F23-02 (Suppress identical alert types within 60s window)
        var key = (SprintId: sprintId, Type: type);
        var now = DateTime.UtcNow;

        if (_lastAlertDispatches.TryGetValue(key, out var lastSentTime))
        {
            var elapsed = now - lastSentTime;
            if (elapsed < TimeSpan.FromSeconds(_options.DebounceSeconds))
            {
                _logger.LogInformation(
                    "Alert {Type} for sprint {SprintId} was triggered {Seconds:F1}s ago; suppressed by {DebounceSeconds}s debounce window.",
                    type,
                    sprintId,
                    elapsed.TotalSeconds,
                    _options.DebounceSeconds);

                return AlertResult.SuppressedByDebounce();
            }
        }

        // 2. Build Protobuf Request (T1-F23-03)
        var request = new TriggerSprintAlertRequest
        {
            SprintId = sprintId,
            Type = type,
            Severity = severity,
            Message = message ?? string.Empty,
            Recommendation = recommendation ?? string.Empty
        };

        // 3. Configure gRPC Call Deadline: T2-F23-03 (2000ms deadline guard)
        var deadline = DateTime.UtcNow.AddMilliseconds(_options.TimeoutMs);
        var callOptions = new CallOptions(
            deadline: deadline,
            cancellationToken: cancellationToken);

        try
        {
            // 4. Execute with Polly Retry Policy: T1-F23-04
            var response = await _retryPolicy.ExecuteAsync(async () =>
            {
                return await _client.TriggerSprintAlertAsync(request, callOptions);
            });

            // 5. Update Debounce Cache: T1-F23-05
            _lastAlertDispatches[key] = DateTime.UtcNow;

            // Audit in repository if configured
            if (_alertRepository != null)
            {
                try
                {
                    await _alertRepository.RecordAlertAsync(new SprintAlertDocument
                    {
                        SprintId = sprintId,
                        AlertType = type.ToString(),
                        Severity = severity.ToString(),
                        Message = message ?? string.Empty,
                        DispatchedAt = DateTime.UtcNow,
                        Acknowledged = response.Acknowledged
                    }, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to persist alert audit log for sprintId={SprintId}", sprintId);
                }
            }

            _logger.LogInformation(
                "Successfully dispatched {Type} alert for sprint {SprintId}. Acknowledged: {Ack}, AlertId: {AlertId}",
                type,
                sprintId,
                response.Acknowledged,
                response.AlertId);

            return AlertResult.Succeeded(response.AlertId, response.Acknowledged);
        }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded)
        {
            // 6. Graceful Degradation: T2-F23-01 & T2-F23-04 (Log warning, DO NOT crash worker)
            _logger.LogWarning(
                ex,
                "MS2 AlertService is currently UNAVAILABLE ({StatusCode}) on {Endpoint}. Alert for sprint {SprintId} ({Type}) dropped gracefully.",
                ex.StatusCode,
                _options.Endpoint,
                sprintId,
                type);

            return AlertResult.Failed($"MS2 AlertService unavailable: {ex.Status.Detail}");
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Unexpected failure triggering alert for sprint {SprintId} ({Type})",
                sprintId,
                type);

            return AlertResult.Failed(ex.Message);
        }
    }
}
