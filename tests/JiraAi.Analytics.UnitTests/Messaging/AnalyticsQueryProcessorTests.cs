namespace JiraAi.Analytics.UnitTests.Messaging;

using System.Text;
using System.Text.Json;
using FluentAssertions;
using JiraAi.Analytics.Contracts.Dtos;
using JiraAi.Analytics.Domain.Models;
using JiraAi.Analytics.Domain.Services;
using JiraAi.Analytics.Infrastructure.Messaging;
using JiraAi.Analytics.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

public class AnalyticsQueryProcessorTests
{
    private readonly InMemorySnapshotRepository _snapshotRepo = new();
    private readonly InMemoryVelocityRepository _velocityRepo = new();
    private readonly IBurndownCalculator _burndownCalculator = new BurndownCalculator();
    private readonly IVelocityCalculator _velocityCalculator = new VelocityCalculator();
    private readonly IHealthScoreCalculator _healthScoreCalculator = new HealthScoreCalculator();
    private readonly IRiskDetectionEngine _riskEngine = new RiskDetectionEngine();
    private readonly IKafkaProducerWrapper _producer = Substitute.For<IKafkaProducerWrapper>();
    private readonly AnalyticsQueryProcessor _sut;

    public AnalyticsQueryProcessorTests()
    {
        var kafkaOptions = Options.Create(new KafkaOptions
        {
            ReplyTopic = "analytics.reply"
        });

        _sut = new AnalyticsQueryProcessor(
            _snapshotRepo,
            _velocityRepo,
            _burndownCalculator,
            _velocityCalculator,
            _healthScoreCalculator,
            _riskEngine,
            _producer,
            kafkaOptions,
            NullLogger<AnalyticsQueryProcessor>.Instance);
    }

    [Fact]
    public async Task ProcessQuery_T1_F22_01_04_ProducesReply_PreservingCorrelationIdHeader()
    {
        // Arrange
        await _snapshotRepo.UpsertSnapshotAsync(new SprintSnapshotDocument
        {
            SprintId = "sprint-query-1",
            ProjectId = "proj-1",
            TotalStoryPoints = 40,
            CompletedStoryPoints = 20,
            RemainingStoryPoints = 20
        });

        var queryJson = JsonSerializer.Serialize(new AnalyticsQueryDto
        {
            QueryId = "q-1",
            CorrelationId = "corr-header-123",
            SprintId = "sprint-query-1",
            QueryType = "BURNDOWN"
        });

        // Act
        var result = await _sut.ProcessQueryAsync(queryJson, correlationIdHeader: "corr-header-123");

        // Assert
        result.Should().BeTrue();
        await _producer.Received(1).ProduceAsync(
            "analytics.reply",
            "sprint-query-1",
            Arg.Is<string>(json => json.Contains("\"correlationId\":\"corr-header-123\"") && json.Contains("\"status\":\"SUCCESS\"")),
            Arg.Is<IDictionary<string, byte[]>>(headers =>
                headers.ContainsKey("kafka_correlationId") &&
                Encoding.UTF8.GetString(headers["kafka_correlationId"]) == "corr-header-123"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ComputeReply_T2_F22_01_UninitializedSprint_ReturnsZeroedMetricsAndStatusSuccess()
    {
        var query = new AnalyticsQueryDto
        {
            QueryId = "q-init",
            CorrelationId = "corr-init",
            SprintId = "sprint-nonexistent",
            QueryType = "BURNDOWN"
        };

        var reply = await _sut.ComputeReplyAsync(query, "corr-init");

        reply.Should().NotBeNull();
        reply.Status.Should().Be("SUCCESS");
        reply.TotalPoints.Should().Be(0);
        reply.CompletedPoints.Should().Be(0);
        reply.RemainingPoints.Should().Be(0);
        reply.HealthScore.Should().Be(100.0);
        reply.Diagnostics.Should().Contain(d => d.Contains("INITIALIZING"));
        reply.BurndownData.Should().NotBeNull();
    }

    [Fact]
    public async Task ProcessQuery_T2_F22_02_MissingCorrelationId_GeneratesFallbackUUID()
    {
        var queryJson = JsonSerializer.Serialize(new AnalyticsQueryDto
        {
            QueryId = "q-no-corr",
            SprintId = "sprint-default",
            QueryType = "BURNDOWN"
        });

        var result = await _sut.ProcessQueryAsync(queryJson, correlationIdHeader: null);
        result.Should().BeTrue();

        await _producer.Received(1).ProduceAsync(
            "analytics.reply",
            "sprint-default",
            Arg.Is<string>(json => !string.IsNullOrEmpty(json)),
            Arg.Is<IDictionary<string, byte[]>>(headers =>
                headers.ContainsKey("kafka_correlationId") && headers["kafka_correlationId"].Length > 0),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessQuery_T2_F22_04_EmptyQueryJson_ReturnsFalseGracefully()
    {
        var result = await _sut.ProcessQueryAsync("");
        result.Should().BeFalse();

        var wsResult = await _sut.ProcessQueryAsync("   ");
        wsResult.Should().BeFalse();
    }

    [Fact]
    public async Task ComputeReply_T2_F22_05_UnrecognizedQueryType_AppendsWarningInDiagnostics()
    {
        var query = new AnalyticsQueryDto
        {
            QueryId = "q-unknown",
            CorrelationId = "corr-unk",
            SprintId = "sprint-unk",
            QueryType = "SOME_CUSTOM_UNRECOGNIZED_QUERY"
        };

        var reply = await _sut.ComputeReplyAsync(query, "corr-unk");
        reply.Diagnostics.Should().Contain(d => d.Contains("Unrecognized queryType"));
    }
}
