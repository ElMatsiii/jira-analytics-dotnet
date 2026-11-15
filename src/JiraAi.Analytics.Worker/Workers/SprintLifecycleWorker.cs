namespace JiraAi.Analytics.Worker.Workers;

using JiraAi.Analytics.Infrastructure.Sprints;
using Microsoft.Extensions.Options;

public class SprintLifecycleOptions
{
    public const string SectionName = "SprintLifecycle";
    /// <summary>How often open snapshots are reconciled with MS2 (sprint dates and closure).</summary>
    public int IntervalSeconds { get; set; } = 60;
}

/// <summary>
/// MS3 receives no event when MS2 completes a sprint, so it periodically asks MS2 (gRPC) about the
/// sprints it is tracking; completed ones are closed and their velocity is recorded.
/// </summary>
public class SprintLifecycleWorker : BackgroundService
{
    private readonly ISprintSnapshotSynchronizer _synchronizer;
    private readonly SprintLifecycleOptions _options;
    private readonly ILogger<SprintLifecycleWorker> _logger;

    public SprintLifecycleWorker(
        ISprintSnapshotSynchronizer synchronizer,
        IOptions<SprintLifecycleOptions> options,
        ILogger<SprintLifecycleWorker> logger)
    {
        _synchronizer = synchronizer;
        _options = options?.Value ?? new SprintLifecycleOptions();
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, _options.IntervalSeconds));
        _logger.LogInformation("SprintLifecycleWorker started; reconciling with MS2 every {Seconds}s.", interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken);
            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>One reconciliation pass; never throws (a failure is logged and retried next interval).</summary>
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var closed = await _synchronizer.ReconcileOpenSnapshotsAsync(cancellationToken);
            if (closed > 0)
            {
                _logger.LogInformation("Closed {Count} completed sprint(s) and recorded their velocity.", closed);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sprint reconciliation with MS2 failed; will retry.");
        }
    }
}
