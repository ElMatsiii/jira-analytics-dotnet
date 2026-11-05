namespace JiraAi.Analytics.UnitTests.Adversarial;

using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using FluentAssertions;
using global::Grpc.Core;
using global::Grpc.Core.Testing;
using JiraAi.Analytics.Contracts.Dtos;
using JiraAi.Analytics.Domain.Models;
using JiraAi.Analytics.Domain.Repositories;
using JiraAi.Analytics.Domain.Services;
using JiraAi.Analytics.Infrastructure.Grpc;
using JiraAi.Analytics.Infrastructure.Messaging;
using JiraAi.Analytics.Infrastructure.Persistence;
using JiraAi.Analytics.Worker.Workers;
using JiraAi.Contracts.Sprints.V1;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

public class ChallengerAdversarialTests
{
    private static AsyncUnaryCall<TResponse> FakeCall<TResponse>(TResponse response)
    {
        return TestCalls.AsyncUnaryCall(
            Task.FromResult(response),
            Task.FromResult(new global::Grpc.Core.Metadata()),
            () => new Status(StatusCode.OK, string.Empty),
            () => new global::Grpc.Core.Metadata(),
            () => { });
    }

    private static AsyncUnaryCall<TResponse> FakeFailingCall<TResponse>(StatusCode statusCode, string detail)
    {
        return TestCalls.AsyncUnaryCall(
            Task.FromException<TResponse>(new RpcException(new Status(statusCode, detail))),
            Task.FromResult(new global::Grpc.Core.Metadata()),
            () => new Status(statusCode, detail),
            () => new global::Grpc.Core.Metadata(),
            () => { });
    }

    #region 1. Kafka Request-Reply Correlation ID & Schema Validation

    [Fact]
    public async Task Adversarial_CorrelationId_HeaderPrecedence_And_Preservation()
    {
        // 1. Arrange: Both header and payload present, header should be preserved
        var snapshotRepo = new InMemorySnapshotRepository();
        var velocityRepo = new InMemoryVelocityRepository();
        var producer = Substitute.For<IKafkaProducerWrapper>();
        var options = Options.Create(new KafkaOptions { ReplyTopic = "analytics.reply" });

        var sut = new AnalyticsQueryProcessor(
            snapshotRepo,
            velocityRepo,
            new BurndownCalculator(),
            new VelocityCalculator(),
            new HealthScoreCalculator(),
            new RiskDetectionEngine(),
            producer,
            options,
            NullLogger<AnalyticsQueryProcessor>.Instance);

        var sprintId = Guid.NewGuid().ToString();
        var headerCorrId = Guid.NewGuid().ToString();
        var bodyCorrId = Guid.NewGuid().ToString();

        var queryJson = JsonSerializer.Serialize(new AnalyticsQueryDto
        {
            QueryId = Guid.NewGuid().ToString(),
            CorrelationId = bodyCorrId,
            SprintId = sprintId,
            QueryType = "BURNDOWN"
        });

        string? capturedReplyJson = null;
        IDictionary<string, byte[]>? capturedHeaders = null;

        await producer.ProduceAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Do<string>(j => capturedReplyJson = j),
            Arg.Do<IDictionary<string, byte[]>>(h => capturedHeaders = h),
            Arg.Any<CancellationToken>());

        // Act
        var result = await sut.ProcessQueryAsync(queryJson, correlationIdHeader: headerCorrId);

        // Assert
        result.Should().BeTrue();
        capturedHeaders.Should().NotBeNull();
        capturedHeaders!.ContainsKey("kafka_correlationId").Should().BeTrue();
        Encoding.UTF8.GetString(capturedHeaders["kafka_correlationId"]).Should().Be(headerCorrId);

        capturedReplyJson.Should().NotBeNull();
        var replyDto = JsonSerializer.Deserialize<AnalyticsReplyDto>(capturedReplyJson!);
        replyDto.Should().NotBeNull();
        replyDto!.CorrelationId.Should().Be(headerCorrId);
        replyDto.SprintId.Should().Be(sprintId);
    }

    [Fact]
    public async Task Adversarial_CorrelationId_FallbackToPayload_WhenHeaderMissing()
    {
        var snapshotRepo = new InMemorySnapshotRepository();
        var velocityRepo = new InMemoryVelocityRepository();
        var producer = Substitute.For<IKafkaProducerWrapper>();
        var options = Options.Create(new KafkaOptions { ReplyTopic = "analytics.reply" });

        var sut = new AnalyticsQueryProcessor(
            snapshotRepo,
            velocityRepo,
            new BurndownCalculator(),
            new VelocityCalculator(),
            new HealthScoreCalculator(),
            new RiskDetectionEngine(),
            producer,
            options,
            NullLogger<AnalyticsQueryProcessor>.Instance);

        var sprintId = Guid.NewGuid().ToString();
        var bodyCorrId = Guid.NewGuid().ToString();

        var queryJson = JsonSerializer.Serialize(new AnalyticsQueryDto
        {
            QueryId = Guid.NewGuid().ToString(),
            CorrelationId = bodyCorrId,
            SprintId = sprintId,
            QueryType = "BURNDOWN"
        });

        string? capturedReplyJson = null;
        IDictionary<string, byte[]>? capturedHeaders = null;

        await producer.ProduceAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Do<string>(j => capturedReplyJson = j),
            Arg.Do<IDictionary<string, byte[]>>(h => capturedHeaders = h),
            Arg.Any<CancellationToken>());

        // Act: correlationIdHeader is null
        var result = await sut.ProcessQueryAsync(queryJson, correlationIdHeader: null);

        // Assert
        result.Should().BeTrue();
        capturedHeaders.Should().NotBeNull();
        Encoding.UTF8.GetString(capturedHeaders!["kafka_correlationId"]).Should().Be(bodyCorrId);

        var replyDto = JsonSerializer.Deserialize<AnalyticsReplyDto>(capturedReplyJson!);
        replyDto!.CorrelationId.Should().Be(bodyCorrId);
    }

    [Fact]
    public async Task Adversarial_CorrelationId_GeneratesValidUuid_WhenBothMissing()
    {
        var snapshotRepo = new InMemorySnapshotRepository();
        var velocityRepo = new InMemoryVelocityRepository();
        var producer = Substitute.For<IKafkaProducerWrapper>();
        var options = Options.Create(new KafkaOptions { ReplyTopic = "analytics.reply" });

        var sut = new AnalyticsQueryProcessor(
            snapshotRepo,
            velocityRepo,
            new BurndownCalculator(),
            new VelocityCalculator(),
            new HealthScoreCalculator(),
            new RiskDetectionEngine(),
            producer,
            options,
            NullLogger<AnalyticsQueryProcessor>.Instance);

        var sprintId = Guid.NewGuid().ToString();

        var queryJson = JsonSerializer.Serialize(new AnalyticsQueryDto
        {
            QueryId = Guid.NewGuid().ToString(),
            CorrelationId = "", // Missing
            SprintId = sprintId,
            QueryType = "BURNDOWN"
        });

        string? capturedReplyJson = null;
        IDictionary<string, byte[]>? capturedHeaders = null;

        await producer.ProduceAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Do<string>(j => capturedReplyJson = j),
            Arg.Do<IDictionary<string, byte[]>>(h => capturedHeaders = h),
            Arg.Any<CancellationToken>());

        // Act: header is whitespace
        var result = await sut.ProcessQueryAsync(queryJson, correlationIdHeader: "   ");

        // Assert
        result.Should().BeTrue();
        capturedHeaders.Should().NotBeNull();
        var headerCorr = Encoding.UTF8.GetString(capturedHeaders!["kafka_correlationId"]);
        Guid.TryParse(headerCorr, out var headerGuid).Should().BeTrue();

        var replyDto = JsonSerializer.Deserialize<AnalyticsReplyDto>(capturedReplyJson!);
        replyDto.Should().NotBeNull();
        Guid.TryParse(replyDto!.CorrelationId, out var bodyGuid).Should().BeTrue();
        headerGuid.Should().Be(bodyGuid);
    }

    [Fact]
    public async Task Adversarial_NonexistentSprint_ReturnsInitializingWithoutCrash()
    {
        var snapshotRepo = new InMemorySnapshotRepository(); // Empty repository
        var velocityRepo = new InMemoryVelocityRepository();
        var producer = Substitute.For<IKafkaProducerWrapper>();
        var options = Options.Create(new KafkaOptions { ReplyTopic = "analytics.reply" });

        var sut = new AnalyticsQueryProcessor(
            snapshotRepo,
            velocityRepo,
            new BurndownCalculator(),
            new VelocityCalculator(),
            new HealthScoreCalculator(),
            new RiskDetectionEngine(),
            producer,
            options,
            NullLogger<AnalyticsQueryProcessor>.Instance);

        var nonexistentSprintId = Guid.NewGuid().ToString();
        var correlationId = Guid.NewGuid().ToString();

        var query = new AnalyticsQueryDto
        {
            QueryId = Guid.NewGuid().ToString(),
            CorrelationId = correlationId,
            SprintId = nonexistentSprintId,
            QueryType = "BURNDOWN"
        };

        // Act
        var reply = await sut.ComputeReplyAsync(query, correlationId);

        // Assert: Valid DTO returned with INITIALIZING diagnostic
        reply.Should().NotBeNull();
        reply.Status.Should().Be("SUCCESS");
        reply.SprintId.Should().Be(nonexistentSprintId);
        reply.CorrelationId.Should().Be(correlationId);
        reply.TotalPoints.Should().Be(0);
        reply.CompletedPoints.Should().Be(0);
        reply.RemainingPoints.Should().Be(0);
        reply.HealthScore.Should().Be(100.0);
        reply.Diagnostics.Should().Contain(d => d.Contains("INITIALIZING"));
        reply.Recommendations.Should().NotBeEmpty();
        reply.BurndownData.Should().NotBeNull();
        Guid.TryParse(reply.ReplyId, out _).Should().BeTrue();
        DateTime.TryParse(reply.Timestamp, out _).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ invalid json syntax ...")]
    [InlineData("{\"sprintId\": 12345, \"corrupted\": true, ")]
    [InlineData("Not even JSON!")]
    public async Task Adversarial_MalformedJson_AnalyticsQuery_SafelyHandledWithoutException(string malformedJson)
    {
        var sut = new AnalyticsQueryProcessor(
            new InMemorySnapshotRepository(),
            new InMemoryVelocityRepository(),
            new BurndownCalculator(),
            new VelocityCalculator(),
            new HealthScoreCalculator(),
            new RiskDetectionEngine(),
            Substitute.For<IKafkaProducerWrapper>(),
            Options.Create(new KafkaOptions()),
            NullLogger<AnalyticsQueryProcessor>.Instance);

        // Act: Should not throw any exception
        var result = await sut.ProcessQueryAsync(malformedJson);

        // Assert: Returns false, gracefully skipped
        result.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not json at all }")]
    [InlineData("{\"eventId\": \"\", \"eventType\": \"ISSUE_CREATED\"}")]
    [InlineData("{\"eventId\": \"123\", \"payload\": null}")]
    public async Task Adversarial_MalformedJson_IssuesEvents_SafelyHandledWithoutException(string malformedJson)
    {
        var sut = new IssuesEventProcessor(
            new InMemorySnapshotRepository(),
            new RiskDetectionEngine(),
            Substitute.For<IAlertClientService>(),
            NullLogger<IssuesEventProcessor>.Instance);

        // Act: Should not throw any exception
        var result = await sut.ProcessEventJsonAsync(malformedJson);

        // Assert: Returns false, gracefully skipped
        result.Should().BeFalse();
    }

    [Fact]
    public async Task Adversarial_WorkerLoops_DoNotCrashOnMalformedJson()
    {
        // Test that both BackgroundService workers consume malformed messages and loop without crashing
        using var cts = new CancellationTokenSource();

        var mockQueryProcessor = Substitute.For<IAnalyticsQueryProcessor>();
        mockQueryProcessor.ProcessQueryAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false)); // Simulates malformed message skipping

        var mockConsumer = Substitute.For<IConsumer<string, string>>();
        int callCount = 0;

        mockConsumer.Consume(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            callCount++;
            if (callCount >= 3)
            {
                cts.Cancel();
                throw new OperationCanceledException();
            }

            return new ConsumeResult<string, string>
            {
                Message = new Message<string, string>
                {
                    Key = "key",
                    Value = "{ corrupt json " + callCount
                },
                Topic = "analytics.query",
                Partition = new Partition(0),
                Offset = new Offset(callCount)
            };
        });

        var worker = new AnalyticsQueryResponderWorker(
            mockQueryProcessor,
            Options.Create(new KafkaOptions()),
            NullLogger<AnalyticsQueryResponderWorker>.Instance,
            mockConsumer);

        // Act
        var workerTask = worker.StartAsync(cts.Token);
        await Task.Delay(150);
        await worker.StopAsync(CancellationToken.None);

        // Assert: Consumed all 3 messages without crash
        callCount.Should().BeGreaterOrEqualTo(3);
        await mockQueryProcessor.Received(2).ProcessQueryAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    #endregion

    #region 2. gRPC Alert Client Resilience & Debounce

    [Fact]
    public async Task Adversarial_Grpc_RapidFireScopeCreep_Within60s_TriggersExactlyOneGrpcCall()
    {
        // Arrange
        var mockClient = Substitute.For<AlertService.AlertServiceClient>();
        mockClient.TriggerSprintAlertAsync(
            Arg.Any<TriggerSprintAlertRequest>(),
            Arg.Any<CallOptions>())
            .Returns(FakeCall(new TriggerSprintAlertResponse
            {
                Acknowledged = true,
                AlertId = "alert-test-uuid"
            }));

        var alertRepo = new InMemoryAlertRepository();
        var options = Options.Create(new AlertClientOptions
        {
            Endpoint = "http://localhost:50052",
            TimeoutMs = 2000,
            DebounceSeconds = 60,
            MaxRetries = 3
        });

        var sut = new AlertClientService(mockClient, options, NullLogger<AlertClientService>.Instance, alertRepo);
        var sprintId = Guid.NewGuid().ToString();

        // Act: 10 rapid-fire alert attempts within milliseconds
        var results = new List<AlertResult>();
        for (int i = 0; i < 10; i++)
        {
            var res = await sut.TriggerSprintAlertAsync(
                sprintId,
                AlertType.ScopeCreep,
                AlertSeverity.Critical,
                $"Scope creep burst #{i}",
                "Review sprint scope");
            results.Add(res);
        }

        // Assert
        results[0].Success.Should().BeTrue();
        results[0].Debounced.Should().BeFalse();

        for (int i = 1; i < 10; i++)
        {
            results[i].Success.Should().BeTrue();
            results[i].Debounced.Should().BeTrue($"Call {i} should be debounced");
        }

        // EXACTLY 1 gRPC call to MS2
        var _ = mockClient.Received(1).TriggerSprintAlertAsync(
            Arg.Is<TriggerSprintAlertRequest>(r => r.SprintId == sprintId && r.Type == AlertType.ScopeCreep),
            Arg.Any<CallOptions>());
    }

    [Fact]
    public async Task Adversarial_Grpc_MS2Unavailable_PollyRetries_GracefulDegradation()
    {
        // Arrange: MS2 throws StatusCode.Unavailable on every call
        var mockClient = Substitute.For<AlertService.AlertServiceClient>();
        int invocationAttempts = 0;

        mockClient.TriggerSprintAlertAsync(
            Arg.Any<TriggerSprintAlertRequest>(),
            Arg.Any<CallOptions>())
            .Returns<AsyncUnaryCall<TriggerSprintAlertResponse>>(_ =>
            {
                invocationAttempts++;
                throw new RpcException(new Status(StatusCode.Unavailable, "MS2 connection refused: localhost:50052"));
            });

        var options = Options.Create(new AlertClientOptions
        {
            Endpoint = "http://localhost:50052",
            TimeoutMs = 2000,
            DebounceSeconds = 60,
            MaxRetries = 2 // 2 retries = 3 total attempts
        });

        var sut = new AlertClientService(mockClient, options, NullLogger<AlertClientService>.Instance);
        var sprintId = Guid.NewGuid().ToString();

        // Act: Must not throw unhandled exception
        var result = await sut.TriggerSprintAlertAsync(
            sprintId,
            AlertType.VelocityDeficit,
            AlertSeverity.Warning,
            "Velocity deficit",
            "Adjust commitments");

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("MS2 AlertService unavailable");
        // Polly retried: initial attempt + 2 retries = 3 invocations
        invocationAttempts.Should().Be(3);
    }

    [Fact]
    public async Task Adversarial_Grpc_DeadlineGuard_ConfiguredOnCallOptions()
    {
        // Verify that CallOptions passed to gRPC client includes the 2000ms deadline guard
        var mockClient = Substitute.For<AlertService.AlertServiceClient>();
        CallOptions? capturedOptions = null;

        mockClient.TriggerSprintAlertAsync(
            Arg.Any<TriggerSprintAlertRequest>(),
            Arg.Do<CallOptions>(opt => capturedOptions = opt))
            .Returns(FakeCall(new TriggerSprintAlertResponse { Acknowledged = true, AlertId = "ack" }));

        var options = Options.Create(new AlertClientOptions
        {
            Endpoint = "http://localhost:50052",
            TimeoutMs = 2000, // 2000ms deadline guard
            DebounceSeconds = 60,
            MaxRetries = 1
        });

        var sut = new AlertClientService(mockClient, options, NullLogger<AlertClientService>.Instance);

        var beforeCall = DateTime.UtcNow;
        await sut.TriggerSprintAlertAsync(
            Guid.NewGuid().ToString(),
            AlertType.BottleneckDetected,
            AlertSeverity.Info,
            "Bottleneck alert",
            "Pair on tasks");
        var afterCall = DateTime.UtcNow;

        // Assert
        capturedOptions.Should().NotBeNull();
        capturedOptions!.Value.Deadline.Should().NotBeNull();

        var deadline = capturedOptions.Value.Deadline!.Value;
        // Deadline should be ~2000ms from beforeCall
        var expectedDeadlineMin = beforeCall.AddMilliseconds(1500);
        var expectedDeadlineMax = afterCall.AddMilliseconds(2500);

        deadline.Should().BeOnOrAfter(expectedDeadlineMin);
        deadline.Should().BeOnOrBefore(expectedDeadlineMax);
    }

    [Fact]
    public async Task Adversarial_Grpc_DeadlineExceeded_GracefulDegradation()
    {
        // Arrange: MS2 hangs and throws StatusCode.DeadlineExceeded
        var mockClient = Substitute.For<AlertService.AlertServiceClient>();

        mockClient.TriggerSprintAlertAsync(
            Arg.Any<TriggerSprintAlertRequest>(),
            Arg.Any<CallOptions>())
            .Returns<AsyncUnaryCall<TriggerSprintAlertResponse>>(_ =>
            {
                throw new RpcException(new Status(StatusCode.DeadlineExceeded, "Deadline of 2000ms exceeded"));
            });

        var options = Options.Create(new AlertClientOptions
        {
            Endpoint = "http://localhost:50052",
            TimeoutMs = 2000,
            DebounceSeconds = 60,
            MaxRetries = 1
        });

        var sut = new AlertClientService(mockClient, options, NullLogger<AlertClientService>.Instance);

        // Act: Should not crash caller
        var result = await sut.TriggerSprintAlertAsync(
            Guid.NewGuid().ToString(),
            AlertType.ScopeCreep,
            AlertSeverity.Critical,
            "Creep",
            "Rec");

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("MS2 AlertService unavailable");
    }

    #endregion
}
