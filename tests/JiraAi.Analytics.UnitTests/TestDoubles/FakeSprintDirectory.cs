namespace JiraAi.Analytics.UnitTests.TestDoubles;

using JiraAi.Analytics.Domain.Models;
using JiraAi.Analytics.Domain.Repositories;
using JiraAi.Analytics.Infrastructure.Sprints;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>In-memory stand-in for MS2's SprintService (gRPC). Unknown sprints return null, like MS2 being unreachable.</summary>
public sealed class FakeSprintDirectory : ISprintDirectory
{
    private readonly Dictionary<string, SprintInfo> _sprints = new();

    public int Calls { get; private set; }

    public FakeSprintDirectory With(SprintInfo info)
    {
        _sprints[info.SprintId] = info;
        return this;
    }

    public Task<SprintInfo?> GetSprintAsync(string sprintId, CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(_sprints.TryGetValue(sprintId, out var info) ? info : null);
    }
}

public static class TestSprintSync
{
    /// <summary>Synchronizer over in-memory repositories; with no directory, behaves as if MS2 were unreachable.</summary>
    public static SprintSnapshotSynchronizer Create(
        ISnapshotRepository snapshots,
        IVelocityRepository velocity,
        ISprintDirectory? directory = null,
        Func<DateTime>? clock = null) =>
        new(directory ?? new FakeSprintDirectory(), snapshots, velocity, NullLogger<SprintSnapshotSynchronizer>.Instance, clock);
}
