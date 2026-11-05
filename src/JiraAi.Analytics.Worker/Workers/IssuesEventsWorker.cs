namespace JiraAi.Analytics.Worker.Workers;

using Confluent.Kafka;
using JiraAi.Analytics.Infrastructure.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public class IssuesEventsWorker : BackgroundService
{
    private readonly IIssuesEventProcessor _processor;
    private readonly KafkaOptions _kafkaOptions;
    private readonly ILogger<IssuesEventsWorker> _logger;
    private readonly IConsumer<string, string>? _injectedConsumer;

    public IssuesEventsWorker(
        IIssuesEventProcessor processor,
        IOptions<KafkaOptions> kafkaOptions,
        ILogger<IssuesEventsWorker> logger)
    {
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _kafkaOptions = kafkaOptions?.Value ?? new KafkaOptions();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public IssuesEventsWorker(
        IIssuesEventProcessor processor,
        IOptions<KafkaOptions> kafkaOptions,
        ILogger<IssuesEventsWorker> logger,
        IConsumer<string, string> consumer)
        : this(processor, kafkaOptions, logger)
    {
        _injectedConsumer = consumer;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yield to allow Web/Host bootstrap
        await Task.Yield();
        _logger.LogInformation("IssuesEventsWorker started. Subscribing to topic: {Topic}", _kafkaOptions.IssuesTopic);

        IConsumer<string, string>? consumer = _injectedConsumer;
        bool ownsConsumer = false;

        if (consumer == null)
        {
            var config = new ConsumerConfig
            {
                BootstrapServers = Environment.GetEnvironmentVariable("KAFKA_BOOTSTRAP_SERVERS")
                    ?? Environment.GetEnvironmentVariable("KAFKA_BROKERS")
                    ?? _kafkaOptions.BootstrapServers,
                GroupId = Environment.GetEnvironmentVariable("KAFKA_ISSUES_GROUP_ID")
                    ?? _kafkaOptions.IssuesGroupId,
                AutoOffsetReset = AutoOffsetReset.Earliest,
                EnableAutoCommit = false,
                EnableAutoOffsetStore = false,
                AllowAutoCreateTopics = true,
                SessionTimeoutMs = 30000,
                HeartbeatIntervalMs = 3000
            };

            try
            {
                consumer = new ConsumerBuilder<string, string>(config).Build();
                consumer.Subscribe(_kafkaOptions.IssuesTopic);
                ownsConsumer = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not initialize live Kafka consumer for {Topic}. Worker will idle until broker available.", _kafkaOptions.IssuesTopic);
            }
        }
        else
        {
            consumer.Subscribe(_kafkaOptions.IssuesTopic);
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (consumer == null)
                {
                    await Task.Delay(2000, stoppingToken);
                    continue;
                }

                try
                {
                    // T1-F21-01..T1-F21-05: Consume and process event
                    var consumeResult = consumer.Consume(stoppingToken);
                    if (consumeResult?.Message != null)
                    {
                        var success = await _processor.ProcessEventJsonAsync(consumeResult.Message.Value, stoppingToken);
                        if (success)
                        {
                            consumer.Commit(consumeResult);
                        }
                    }
                }
                catch (ConsumeException ex)
                {
                    _logger.LogWarning("Kafka consume warning on topic {Topic}: {Reason}", _kafkaOptions.IssuesTopic, ex.Error.Reason);
                    await Task.Delay(500, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // T2-F24-02: DB/Processing exception handling does not crash worker
                    _logger.LogError(ex, "Unexpected error in IssuesEventsWorker loop. Continuing.");
                    await Task.Delay(1000, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // T2-F24-03: Graceful shutdown
            _logger.LogInformation("IssuesEventsWorker cancellation requested. Shutting down gracefully.");
        }
        finally
        {
            if (ownsConsumer && consumer != null)
            {
                try
                {
                    consumer.Close();
                    consumer.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error closing Kafka consumer.");
                }
            }
        }
    }
}
