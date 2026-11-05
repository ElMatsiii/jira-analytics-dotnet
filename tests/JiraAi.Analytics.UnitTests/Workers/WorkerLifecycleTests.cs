namespace JiraAi.Analytics.UnitTests.Workers;

using Confluent.Kafka;
using FluentAssertions;
using JiraAi.Analytics.Infrastructure.Messaging;
using JiraAi.Analytics.Worker.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

public class WorkerLifecycleTests
{
    private readonly IIssuesEventProcessor _mockEventProcessor = Substitute.For<IIssuesEventProcessor>();
    private readonly IAnalyticsQueryProcessor _mockQueryProcessor = Substitute.For<IAnalyticsQueryProcessor>();
    private readonly IConsumer<string, string> _mockConsumer = Substitute.For<IConsumer<string, string>>();
    private readonly IOptions<KafkaOptions> _options = Options.Create(new KafkaOptions());

    [Fact]
    public async Task IssuesEventsWorker_T2_F24_03_ConsumesMessageAndStopsGracefullyOnCancellation()
    {
        // Arrange
        using var cts = new CancellationTokenSource();

        var message = new Message<string, string>
        {
            Key = "key-1",
            Value = "{\"eventId\":\"evt-1\"}"
        };

        var consumeResult = new ConsumeResult<string, string>
        {
            Message = message,
            Topic = "issues.events",
            Partition = new Partition(0),
            Offset = new Offset(10)
        };

        // First consume returns message, second consume cancels
        _mockConsumer.Consume(Arg.Any<CancellationToken>())
            .Returns(
                _ =>
                {
                    cts.Cancel(); // Cancel after first consume
                    return consumeResult;
                },
                _ => throw new OperationCanceledException());

        _mockEventProcessor.ProcessEventJsonAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var worker = new IssuesEventsWorker(
            _mockEventProcessor,
            _options,
            NullLogger<IssuesEventsWorker>.Instance,
            _mockConsumer);

        // Act
        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        await worker.StopAsync(CancellationToken.None);

        // Assert
        await _mockEventProcessor.Received().ProcessEventJsonAsync(message.Value, Arg.Any<CancellationToken>());
        _mockConsumer.Received().Commit(consumeResult);
    }

    [Fact]
    public async Task AnalyticsQueryResponderWorker_ConsumesQueryAndStopsGracefullyOnCancellation()
    {
        // Arrange
        using var cts = new CancellationTokenSource();

        var message = new Message<string, string>
        {
            Key = "sprint-1",
            Value = "{\"queryId\":\"q-1\"}"
        };

        var consumeResult = new ConsumeResult<string, string>
        {
            Message = message,
            Topic = "analytics.query",
            Partition = new Partition(0),
            Offset = new Offset(5)
        };

        _mockConsumer.Consume(Arg.Any<CancellationToken>())
            .Returns(
                _ =>
                {
                    cts.Cancel();
                    return consumeResult;
                },
                _ => throw new OperationCanceledException());

        _mockQueryProcessor.ProcessQueryAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var worker = new AnalyticsQueryResponderWorker(
            _mockQueryProcessor,
            _options,
            NullLogger<AnalyticsQueryResponderWorker>.Instance,
            _mockConsumer);

        // Act
        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        await worker.StopAsync(CancellationToken.None);

        // Assert
        await _mockQueryProcessor.Received().ProcessQueryAsync(message.Value, Arg.Any<string?>(), Arg.Any<CancellationToken>());
        _mockConsumer.Received().Commit(consumeResult);
    }

    [Fact]
    public async Task IssuesEventsWorker_T2_F24_02_ConsumeException_HandledWithoutCrashingWorker()
    {
        using var cts = new CancellationTokenSource();

        _mockConsumer.Consume(Arg.Any<CancellationToken>())
            .Returns(
                _ => throw new ConsumeException(new ConsumeResult<byte[], byte[]>(), new Error(ErrorCode.Local_QueueFull, "Queue full")),
                _ =>
                {
                    cts.Cancel();
                    throw new OperationCanceledException();
                });

        var worker = new IssuesEventsWorker(
            _mockEventProcessor,
            _options,
            NullLogger<IssuesEventsWorker>.Instance,
            _mockConsumer);

        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AnalyticsQueryResponderWorker_ConsumeException_HandledWithoutCrashingWorker()
    {
        using var cts = new CancellationTokenSource();

        _mockConsumer.Consume(Arg.Any<CancellationToken>())
            .Returns(
                _ => throw new ConsumeException(new ConsumeResult<byte[], byte[]>(), new Error(ErrorCode.Local_QueueFull, "Queue full")),
                _ =>
                {
                    cts.Cancel();
                    throw new OperationCanceledException();
                });

        var worker = new AnalyticsQueryResponderWorker(
            _mockQueryProcessor,
            _options,
            NullLogger<AnalyticsQueryResponderWorker>.Instance,
            _mockConsumer);

        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        await worker.StopAsync(CancellationToken.None);
    }
}
