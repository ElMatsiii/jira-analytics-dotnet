namespace JiraAi.Analytics.Worker.Workers;

using System.Text;
using Confluent.Kafka;
using JiraAi.Analytics.Infrastructure.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public class AnalyticsQueryResponderWorker : BackgroundService
{
    private readonly IAnalyticsQueryProcessor _queryProcessor;
    private readonly KafkaOptions _kafkaOptions;
    private readonly ILogger<AnalyticsQueryResponderWorker> _logger;
    private readonly IConsumer<string, string>? _injectedConsumer;

    public AnalyticsQueryResponderWorker(
        IAnalyticsQueryProcessor queryProcessor,
        IOptions<KafkaOptions> kafkaOptions,
        ILogger<AnalyticsQueryResponderWorker> logger)
    {
        _queryProcessor = queryProcessor ?? throw new ArgumentNullException(nameof(queryProcessor));
        _kafkaOptions = kafkaOptions?.Value ?? new KafkaOptions();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public AnalyticsQueryResponderWorker(
        IAnalyticsQueryProcessor queryProcessor,
        IOptions<KafkaOptions> kafkaOptions,
        ILogger<AnalyticsQueryResponderWorker> logger,
        IConsumer<string, string> consumer)
        : this(queryProcessor, kafkaOptions, logger)
    {
        _injectedConsumer = consumer;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        _logger.LogInformation("AnalyticsQueryResponderWorker started. Subscribing to topic: {Topic}", _kafkaOptions.QueryTopic);

        IConsumer<string, string>? consumer = _injectedConsumer;
        bool ownsConsumer = false;

        if (consumer == null)
        {
            var config = new ConsumerConfig
            {
                BootstrapServers = Environment.GetEnvironmentVariable("KAFKA_BOOTSTRAP_SERVERS")
                    ?? Environment.GetEnvironmentVariable("KAFKA_BROKERS")
                    ?? _kafkaOptions.BootstrapServers,
                GroupId = Environment.GetEnvironmentVariable("KAFKA_QUERY_GROUP_ID")
                    ?? _kafkaOptions.QueryGroupId,
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
                consumer.Subscribe(_kafkaOptions.QueryTopic);
                ownsConsumer = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not initialize live Kafka consumer for {Topic}. Worker will idle until broker available.", _kafkaOptions.QueryTopic);
            }
        }
        else
        {
            consumer.Subscribe(_kafkaOptions.QueryTopic);
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
                    // T1-F22-01: Poll message from analytics.query
                    var consumeResult = consumer.Consume(stoppingToken);
                    if (consumeResult?.Message != null)
                    {
                        // T1-F22-02: Extract kafka_correlationId header
                        string? correlationId = null;
                        if (consumeResult.Message.Headers != null &&
                            consumeResult.Message.Headers.TryGetLastBytes("kafka_correlationId", out var corrBytes))
                        {
                            correlationId = Encoding.UTF8.GetString(corrBytes);
                        }

                        var success = await _queryProcessor.ProcessQueryAsync(
                            consumeResult.Message.Value,
                            correlationId,
                            stoppingToken);

                        if (success)
                        {
                            consumer.Commit(consumeResult);
                        }
                    }
                }
                catch (ConsumeException ex)
                {
                    _logger.LogWarning("Kafka consume warning on topic {Topic}: {Reason}", _kafkaOptions.QueryTopic, ex.Error.Reason);
                    await Task.Delay(500, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unexpected error in AnalyticsQueryResponderWorker loop. Continuing.");
                    await Task.Delay(1000, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("AnalyticsQueryResponderWorker received cancellation signal. Shutting down.");
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
                    _logger.LogDebug(ex, "Error closing query responder Kafka consumer.");
                }
            }
        }
    }
}
