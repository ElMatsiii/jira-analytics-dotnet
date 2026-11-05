namespace JiraAi.Analytics.UnitTests.Grpc;

using FluentAssertions;
using global::Grpc.Core;
using global::Grpc.Core.Testing;
using JiraAi.Analytics.Domain.Repositories;
using JiraAi.Analytics.Infrastructure.Grpc;
using JiraAi.Analytics.Infrastructure.Persistence;
using JiraAi.Contracts.Sprints.V1;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

public class AlertClientServiceTests
{
    private readonly AlertService.AlertServiceClient _mockClient = Substitute.For<AlertService.AlertServiceClient>();
    private readonly IAlertRepository _alertRepo = new InMemoryAlertRepository();
    private readonly IOptions<AlertClientOptions> _options;

    public AlertClientServiceTests()
    {
        _options = Options.Create(new AlertClientOptions
        {
            Endpoint = "http://localhost:50052",
            TimeoutMs = 2000,
            DebounceSeconds = 60,
            MaxRetries = 1 // 1 retry in tests for speed
        });
    }

    private static AsyncUnaryCall<TResponse> FakeCall<TResponse>(TResponse response)
    {
        return TestCalls.AsyncUnaryCall(
            Task.FromResult(response),
            Task.FromResult(new Metadata()),
            () => new Status(StatusCode.OK, string.Empty),
            () => new Metadata(),
            () => { });
    }

    [Fact]
    public async Task TriggerSprintAlert_T1_F23_01_04_ScopeCreepAlert_DispatchedAndParsed()
    {
        // Arrange
        _mockClient.TriggerSprintAlertAsync(
            Arg.Any<TriggerSprintAlertRequest>(),
            Arg.Any<CallOptions>())
            .Returns(FakeCall(new TriggerSprintAlertResponse
            {
                Acknowledged = true,
                AlertId = "alert-ack-100"
            }));

        var sut = new AlertClientService(_mockClient, _options, NullLogger<AlertClientService>.Instance, _alertRepo);

        // Act
        var result = await sut.TriggerSprintAlertAsync(
            sprintId: "sprint-sc-1",
            type: AlertType.ScopeCreep,
            severity: AlertSeverity.Warning,
            message: "Scope creep > 20%",
            recommendation: "Reprioritize backlog");

        // Assert
        result.Success.Should().BeTrue();
        result.Acknowledged.Should().BeTrue();
        result.AlertId.Should().Be("alert-ack-100");
        result.Debounced.Should().BeFalse();

        // T1-F23-05: Recorded in audit
        var lastTime = await _alertRepo.GetLastAlertTimeAsync("sprint-sc-1", AlertType.ScopeCreep.ToString());
        lastTime.Should().NotBeNull();
    }

    [Fact]
    public async Task TriggerSprintAlert_T2_F23_02_Debounce_SuppressesDuplicateAlertWithin60s()
    {
        _mockClient.TriggerSprintAlertAsync(
            Arg.Any<TriggerSprintAlertRequest>(),
            Arg.Any<CallOptions>())
            .Returns(FakeCall(new TriggerSprintAlertResponse { Acknowledged = true, AlertId = "alert-1" }));

        var sut = new AlertClientService(_mockClient, _options, NullLogger<AlertClientService>.Instance, _alertRepo);

        // 1st dispatch
        var result1 = await sut.TriggerSprintAlertAsync("sprint-debounce", AlertType.ScopeCreep, AlertSeverity.Warning, "Creep 1", "Rec 1");
        result1.Success.Should().BeTrue();
        result1.Debounced.Should().BeFalse();

        // 2nd dispatch immediately
        var result2 = await sut.TriggerSprintAlertAsync("sprint-debounce", AlertType.ScopeCreep, AlertSeverity.Warning, "Creep 2", "Rec 2");
        result2.Success.Should().BeTrue();
        result2.Debounced.Should().BeTrue(); // Suppressed!

        // Assert gRPC client only called once
        var _ = _mockClient.Received(1).TriggerSprintAlertAsync(Arg.Any<TriggerSprintAlertRequest>(), Arg.Any<CallOptions>());
    }

    [Fact]
    public async Task TriggerSprintAlert_T2_F23_01_04_MS2Unavailable_HandledGracefullyWithoutCrash()
    {
        // Simulate gRPC UNAVAILABLE RpcException
        _mockClient.TriggerSprintAlertAsync(
            Arg.Any<TriggerSprintAlertRequest>(),
            Arg.Any<CallOptions>())
            .Returns<AsyncUnaryCall<TriggerSprintAlertResponse>>(_ =>
            {
                throw new RpcException(new Status(StatusCode.Unavailable, "Connection refused"));
            });

        var sut = new AlertClientService(_mockClient, _options, NullLogger<AlertClientService>.Instance, _alertRepo);

        // Act
        var result = await sut.TriggerSprintAlertAsync(
            "sprint-down",
            AlertType.VelocityDeficit,
            AlertSeverity.Warning,
            "Deficit",
            "Rec");

        // Assert: Graceful degradation, worker does not crash
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("MS2 AlertService unavailable");
    }

    [Fact]
    public async Task TriggerSprintAlert_EmptySprintId_ThrowsArgumentException()
    {
        var sut = new AlertClientService(_mockClient, _options, NullLogger<AlertClientService>.Instance);
        var act = async () => await sut.TriggerSprintAlertAsync("", AlertType.ScopeCreep, AlertSeverity.Warning, "msg", "rec");
        await act.Should().ThrowAsync<ArgumentException>();
    }
}
