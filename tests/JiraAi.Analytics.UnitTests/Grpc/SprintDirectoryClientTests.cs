namespace JiraAi.Analytics.UnitTests.Grpc;

using FluentAssertions;
using global::Grpc.Core;
using global::Grpc.Core.Testing;
using JiraAi.Analytics.Domain.Models;
using JiraAi.Analytics.Infrastructure.Grpc;
using JiraAi.Contracts.Sprints.V1;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

public class SprintDirectoryClientTests
{
    private readonly SprintService.SprintServiceClient _mockClient = Substitute.For<SprintService.SprintServiceClient>();

    private SprintDirectoryClient CreateSut() =>
        new(_mockClient, Options.Create(new SprintDirectoryOptions { TimeoutMs = 500 }), NullLogger<SprintDirectoryClient>.Instance);

    private static AsyncUnaryCall<GetSprintResponse> FakeCall(GetSprintResponse response) =>
        TestCalls.AsyncUnaryCall(
            Task.FromResult(response),
            Task.FromResult(new Metadata()),
            () => new Status(StatusCode.OK, string.Empty),
            () => new Metadata(),
            () => { });

    private static AsyncUnaryCall<GetSprintResponse> FailingCall(StatusCode code) =>
        TestCalls.AsyncUnaryCall(
            Task.FromException<GetSprintResponse>(new RpcException(new Status(code, "boom"))),
            Task.FromResult(new Metadata()),
            () => new Status(code, "boom"),
            () => new Metadata(),
            () => { });

    // 2026-11-16T00:00:00Z and 2026-11-29T23:59:59Z in epoch milliseconds, as MS2 sends them
    private const long StartMs = 1_794_787_200_000;
    private const long EndMs = 1_795_996_799_000;

    [Fact]
    public async Task GetSprint_ConvertsMs2ProtoToLocalModel()
    {
        _mockClient.GetSprintAsync(Arg.Is<GetSprintRequest>(r => r.Id == "sprint-1"), Arg.Any<CallOptions>())
            .Returns(FakeCall(new GetSprintResponse
            {
                Sprint = new Sprint
                {
                    Id = "sprint-1", ProjectId = "project-1", Name = "Sprint Pagos",
                    StartDate = StartMs, EndDate = EndMs, Status = SprintStatus.Active
                }
            }));

        var info = await CreateSut().GetSprintAsync("sprint-1");

        info.Should().Be(new SprintInfo(
            "sprint-1", "project-1", "Sprint Pagos",
            new DateTime(2026, 11, 16, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 11, 29, 23, 59, 59, DateTimeKind.Utc),
            SprintLifecycleStatus.Active));
        info!.StartDate.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public async Task GetSprint_SetsADeadlineOnTheCall()
    {
        CallOptions captured = default;
        _mockClient.GetSprintAsync(Arg.Any<GetSprintRequest>(), Arg.Do<CallOptions>(o => captured = o))
            .Returns(FakeCall(new GetSprintResponse { Sprint = new Sprint { Id = "s", StartDate = StartMs, EndDate = EndMs } }));

        await CreateSut().GetSprintAsync("s");

        captured.Deadline.Should().NotBeNull();
        captured.Deadline!.Value.Should().BeCloseTo(DateTime.UtcNow.AddMilliseconds(500), TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData(StatusCode.NotFound)]
    [InlineData(StatusCode.Unavailable)]
    [InlineData(StatusCode.DeadlineExceeded)]
    public async Task GetSprint_ReturnsNullWhenMs2CannotAnswer(StatusCode code)
    {
        _mockClient.GetSprintAsync(Arg.Any<GetSprintRequest>(), Arg.Any<CallOptions>()).Returns(FailingCall(code));

        (await CreateSut().GetSprintAsync("sprint-1")).Should().BeNull();
    }

    [Fact]
    public async Task GetSprint_ReturnsNullForEmptyIdOrEmptyResponse()
    {
        (await CreateSut().GetSprintAsync("  ")).Should().BeNull();
        _mockClient.DidNotReceive().GetSprintAsync(Arg.Any<GetSprintRequest>(), Arg.Any<CallOptions>());

        _mockClient.GetSprintAsync(Arg.Any<GetSprintRequest>(), Arg.Any<CallOptions>()).Returns(FakeCall(new GetSprintResponse()));
        (await CreateSut().GetSprintAsync("sprint-1")).Should().BeNull();
    }

    [Fact]
    public void ToSprintInfo_FallsBackWhenMs2SendsNoOrInvalidDates()
    {
        var noEnd = SprintDirectoryClient.ToSprintInfo(new Sprint { Id = "s", StartDate = StartMs, EndDate = 0 });
        noEnd.EndDate.Should().Be(noEnd.StartDate.AddDays(14));

        var endBeforeStart = SprintDirectoryClient.ToSprintInfo(new Sprint { Id = "s", StartDate = EndMs, EndDate = StartMs });
        endBeforeStart.EndDate.Should().Be(endBeforeStart.StartDate.AddDays(14));

        var noStart = SprintDirectoryClient.ToSprintInfo(new Sprint { Id = "s", StartDate = 0, EndDate = 0 });
        noStart.StartDate.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Theory]
    [InlineData(SprintStatus.Planned, SprintLifecycleStatus.Planned)]
    [InlineData(SprintStatus.Active, SprintLifecycleStatus.Active)]
    [InlineData(SprintStatus.Completed, SprintLifecycleStatus.Completed)]
    [InlineData(SprintStatus.Cancelled, SprintLifecycleStatus.Cancelled)]
    [InlineData(SprintStatus.Unspecified, SprintLifecycleStatus.Unknown)]
    public void ToLifecycle_MapsEveryProtoStatus(SprintStatus proto, SprintLifecycleStatus expected)
    {
        SprintDirectoryClient.ToLifecycle(proto).Should().Be(expected);
    }

    [Fact]
    public void FromEpochMilliseconds_TreatsZeroAsMissing()
    {
        SprintDirectoryClient.FromEpochMilliseconds(0).Should().BeNull();
        SprintDirectoryClient.FromEpochMilliseconds(StartMs).Should().Be(new DateTime(2026, 11, 16, 0, 0, 0, DateTimeKind.Utc));
    }

    [Theory]
    [InlineData(RiskType.ScopeCreep, AlertType.ScopeCreep)]
    [InlineData(RiskType.VelocityDeficit, AlertType.VelocityDeficit)]
    [InlineData(RiskType.BottleneckDetected, AlertType.BottleneckDetected)]
    [InlineData(RiskType.Unspecified, AlertType.Unspecified)]
    public void AlertMapping_ConvertsRiskTypeToMs2Proto(RiskType local, AlertType proto)
    {
        AlertMapping.ToProto(local).Should().Be(proto);
    }

    [Theory]
    [InlineData(RiskSeverity.Info, AlertSeverity.Info)]
    [InlineData(RiskSeverity.Warning, AlertSeverity.Warning)]
    [InlineData(RiskSeverity.Critical, AlertSeverity.Critical)]
    [InlineData(RiskSeverity.Unspecified, AlertSeverity.Unspecified)]
    public void AlertMapping_ConvertsRiskSeverityToMs2Proto(RiskSeverity local, AlertSeverity proto)
    {
        AlertMapping.ToProto(local).Should().Be(proto);
    }
}
