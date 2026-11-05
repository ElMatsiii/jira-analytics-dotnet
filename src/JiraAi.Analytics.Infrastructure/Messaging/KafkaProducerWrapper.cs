namespace JiraAi.Analytics.Infrastructure.Messaging;

using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public interface IKafkaProducerWrapper : IDisposable
{
    Task ProduceAsync(
        string topic,
        string key,
        string value,
        IDictionary<string, byte[]>? headers = null,
        CancellationToken cancellationToken = default);
}

public class KafkaProducerWrapper : IKafkaProducerWrapper
{
    private readonly IProducer<string, string> _producer;
    private readonly ILogger<KafkaProducerWrapper> _logger;

    public KafkaProducerWrapper(IOptions<KafkaOptions> options, ILogger<KafkaProducerWrapper> logger)
    {
        _logger = logger;
        var config = new ProducerConfig
        {
            BootstrapServers = Environment.GetEnvironmentVariable("KAFKA_BOOTSTRAP_SERVERS")
                ?? Environment.GetEnvironmentVariable("KAFKA_BROKERS")
                ?? options.Value.BootstrapServers,
            Acks = Acks.All,
            EnableIdempotence = true,
            MessageSendMaxRetries = 3,
            RetryBackoffMs = 200
        };

        _producer = new ProducerBuilder<string, string>(config).Build();
    }

    public KafkaProducerWrapper(IProducer<string, string> producer, ILogger<KafkaProducerWrapper> logger)
    {
        _producer = producer ?? throw new ArgumentNullException(nameof(producer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task ProduceAsync(
        string topic,
        string key,
        string value,
        IDictionary<string, byte[]>? headers = null,
        CancellationToken cancellationToken = default)
    {
        var message = new Message<string, string>
        {
            Key = key,
            Value = value,
            Timestamp = new Timestamp(DateTime.UtcNow)
        };

        if (headers != null && headers.Count > 0)
        {
            message.Headers = new Headers();
            foreach (var kvp in headers)
            {
                message.Headers.Add(kvp.Key, kvp.Value);
            }
        }

        try
        {
            var deliveryResult = await _producer.ProduceAsync(topic, message, cancellationToken);
            _logger.LogDebug(
                "Produced Kafka message to {Topic} [partition {Partition}] @ offset {Offset}",
                topic,
                deliveryResult.Partition.Value,
                deliveryResult.Offset.Value);
        }
        catch (ProduceException<string, string> ex)
        {
            _logger.LogError(ex, "Failed to produce message to {Topic} with key {Key}: {Reason}", topic, key, ex.Error.Reason);
            throw;
        }
    }

    public void Dispose()
    {
        _producer.Flush(TimeSpan.FromSeconds(5));
        _producer.Dispose();
    }
}
