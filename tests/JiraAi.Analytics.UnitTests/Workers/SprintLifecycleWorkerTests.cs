namespace JiraAi.Analytics.UnitTests.Workers;

using FluentAssertions;
using JiraAi.Analytics.Infrastructure.Sprints;
using JiraAi.Analytics.Worker.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

public class SprintLifecycleWorkerTests
{
    private readonly ISprintSnapshotSynchronizer _sync = Substitute.For<ISprintSnapshotSynchronizer>();

    private SprintLifecycleWorker CreateSut(int intervalSeconds = 60) =>
        new(_sync, Options.Create(new SprintLifecycleOptions { IntervalSeconds = intervalSeconds }), NullLogger<SprintLifecycleWorker>.Instance);

    [Fact]
    public async Task RunOnce_ReconcilesOpenSnapshots()
    {
        _sync.ReconcileOpenSnapshotsAsync(Arg.Any<CancellationToken>()).Returns(2);

        await CreateSut().RunOnceAsync(CancellationToken.None);

        await _sync.Received(1).ReconcileOpenSnapshotsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunOnce_NeverThrows_WhenReconciliationFails()
    {
        _sync.ReconcileOpenSnapshotsAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("MS2 down"));

        var act = async () => await CreateSut().RunOnceAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RunOnce_SwallowsCancellationDuringShutdown()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _sync.ReconcileOpenSnapshotsAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new OperationCanceledException());

        var act = async () => await CreateSut().RunOnceAsync(cts.Token);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Execute_RunsImmediatelyAndStopsOnCancellation()
    {
        var sut = CreateSut(intervalSeconds: 1); // clamped to the 5 s minimum
        using var cts = new CancellationTokenSource();

        await sut.StartAsync(cts.Token);
        await Task.Delay(200);
        cts.Cancel();
        await sut.StopAsync(CancellationToken.None);

        await _sync.Received(1).ReconcileOpenSnapshotsAsync(Arg.Any<CancellationToken>());
    }
}
