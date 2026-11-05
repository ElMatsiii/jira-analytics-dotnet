namespace JiraAi.Analytics.UnitTests.Messaging;

using System.Text;
using Confluent.Kafka;
using FluentAssertions;
using JiraAi.Analytics.Infrastructure.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

public class KafkaProducerWrapperTests
{
    private readonly IProducer<string, string> _mockProducer = Substitute.For<IProducer<string, string>>();

    [Fact]
    public async Task ProduceAsync_ProducesMessageWithHeaders()
    {
        _mockProducer.ProduceAsync(Arg.Any<string>(), Arg.Any<Message<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new DeliveryResult<string, string>
            {
                Topic = "analytics.reply",
                Partition = new Partition(0),
                Offset = new Offset(123),
                Status = PersistenceStatus.Persisted
            }));

        var sut = new KafkaProducerWrapper(_mockProducer, NullLogger<KafkaProducerWrapper>.Instance);

        var headers = new Dictionary<string, byte[]>
        {
            { "kafka_correlationId", Encoding.UTF8.GetBytes("corr-999") }
        };

        await sut.ProduceAsync("analytics.reply", "sprint-1", "{\"status\":\"SUCCESS\"}", headers);

        await _mockProducer.Received(1).ProduceAsync(
            "analytics.reply",
            Arg.Is<Message<string, string>>(m =>
                m.Key == "sprint-1" &&
                m.Headers.Any(h => h.Key == "kafka_correlationId")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProduceAsync_ThrowsProduceException_WhenKafkaFails()
    {
        _mockProducer.ProduceAsync(Arg.Any<string>(), Arg.Any<Message<string, string>>(), Arg.Any<CancellationToken>())
            .Returns<Task<DeliveryResult<string, string>>>(_ =>
            {
                throw new ProduceException<string, string>(new Error(ErrorCode.BrokerNotAvailable, "Broker down"), new DeliveryResult<string, string>());
            });

        var sut = new KafkaProducerWrapper(_mockProducer, NullLogger<KafkaProducerWrapper>.Instance);

        var act = async () => await sut.ProduceAsync("analytics.reply", "sprint-1", "{}");
        await act.Should().ThrowAsync<ProduceException<string, string>>();
    }

    [Fact]
    public void Dispose_FlushesAndDisposesUnderlyingProducer()
    {
        var sut = new KafkaProducerWrapper(_mockProducer, NullLogger<KafkaProducerWrapper>.Instance);
        sut.Dispose();

        _mockProducer.Received(1).Flush(Arg.Any<TimeSpan>());
        _mockProducer.Received(1).Dispose();
    }
}
