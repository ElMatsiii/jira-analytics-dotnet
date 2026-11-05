namespace JiraAi.Analytics.Infrastructure.Persistence;

using JiraAi.Analytics.Domain.Models;
using JiraAi.Analytics.Domain.Repositories;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

public class MongoSnapshotRepository : ISnapshotRepository
{
    private readonly IMongoDbContext _context;
    private readonly ILogger<MongoSnapshotRepository> _logger;

    public MongoSnapshotRepository(IMongoDbContext context, ILogger<MongoSnapshotRepository> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<SprintSnapshotDocument?> GetBySprintIdAsync(string sprintId, CancellationToken cancellationToken = default)
    {
        // T2-F20-03: Querying non-existent sprintId returns null instead of throwing
        if (string.IsNullOrWhiteSpace(sprintId))
        {
            return null;
        }

        try
        {
            var filter = Builders<SprintSnapshotDocument>.Filter.Eq(x => x.SprintId, sprintId);
            return await _context.Snapshots.Find(filter).FirstOrDefaultAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve snapshot for sprintId={SprintId}", sprintId);
            throw;
        }
    }

    public async Task UpsertSnapshotAsync(SprintSnapshotDocument snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        try
        {
            snapshot.UpdatedAt = DateTime.UtcNow;
            snapshot.Version++;

            // T2-F20-02: Concurrent upsert idempotency via unique filter on SprintId
            var filter = Builders<SprintSnapshotDocument>.Filter.Eq(x => x.SprintId, snapshot.SprintId);
            var options = new ReplaceOptions { IsUpsert = true };

            await _context.Snapshots.ReplaceOneAsync(filter, snapshot, options, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upsert snapshot for sprintId={SprintId}", snapshot.SprintId);
            throw;
        }
    }

    public async Task AppendDailyBurndownAsync(string sprintId, DailyBurndownRecord burndownRecord, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(burndownRecord);

        try
        {
            var snapshot = await GetBySprintIdAsync(sprintId, cancellationToken);
            if (snapshot != null)
            {
                var existingIndex = snapshot.DailyBurndown.FindIndex(d => d.Date == burndownRecord.Date);
                if (existingIndex >= 0)
                {
                    snapshot.DailyBurndown[existingIndex] = burndownRecord;
                }
                else
                {
                    snapshot.DailyBurndown.Add(burndownRecord);
                }

                await UpsertSnapshotAsync(snapshot, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to append daily burndown for sprintId={SprintId}", sprintId);
            throw;
        }
    }

    public async Task<List<SprintSnapshotDocument>> GetActiveSnapshotsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var filter = Builders<SprintSnapshotDocument>.Filter.Eq(x => x.Status, "ACTIVE");
            return await _context.Snapshots.Find(filter).ToListAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve active sprint snapshots");
            throw;
        }
    }
}

public class MongoVelocityRepository : IVelocityRepository
{
    private readonly IMongoDbContext _context;
    private readonly ILogger<MongoVelocityRepository> _logger;

    public MongoVelocityRepository(IMongoDbContext context, ILogger<MongoVelocityRepository> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task RecordVelocityAsync(VelocityRecordDocument velocityRecord, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(velocityRecord);

        try
        {
            var filter = Builders<VelocityRecordDocument>.Filter.And(
                Builders<VelocityRecordDocument>.Filter.Eq(x => x.ProjectId, velocityRecord.ProjectId),
                Builders<VelocityRecordDocument>.Filter.Eq(x => x.SprintId, velocityRecord.SprintId));

            var options = new ReplaceOptions { IsUpsert = true };
            await _context.Velocity.ReplaceOneAsync(filter, velocityRecord, options, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record velocity for sprintId={SprintId}", velocityRecord.SprintId);
            throw;
        }
    }

    public async Task<List<VelocityRecordDocument>> GetRecentCompletedSprintsAsync(string projectId, int limit = 3, CancellationToken cancellationToken = default)
    {
        try
        {
            FilterDefinition<VelocityRecordDocument> filter;
            if (string.IsNullOrWhiteSpace(projectId))
            {
                filter = Builders<VelocityRecordDocument>.Filter.Empty;
            }
            else
            {
                filter = Builders<VelocityRecordDocument>.Filter.Eq(x => x.ProjectId, projectId);
            }

            return await _context.Velocity
                .Find(filter)
                .SortByDescending(x => x.CompletedDate)
                .Limit(limit)
                .ToListAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query recent velocity records for projectId={ProjectId}", projectId);
            throw;
        }
    }
}

public class MongoAlertRepository : IAlertRepository
{
    private readonly IMongoDbContext _context;
    private readonly ILogger<MongoAlertRepository> _logger;

    public MongoAlertRepository(IMongoDbContext context, ILogger<MongoAlertRepository> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task RecordAlertAsync(SprintAlertDocument alert, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alert);

        try
        {
            await _context.Alerts.InsertOneAsync(alert, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record alert for sprintId={SprintId}", alert.SprintId);
            throw;
        }
    }

    public async Task<DateTime?> GetLastAlertTimeAsync(string sprintId, string alertType, CancellationToken cancellationToken = default)
    {
        try
        {
            var filter = Builders<SprintAlertDocument>.Filter.And(
                Builders<SprintAlertDocument>.Filter.Eq(x => x.SprintId, sprintId),
                Builders<SprintAlertDocument>.Filter.Eq(x => x.AlertType, alertType));

            var latest = await _context.Alerts
                .Find(filter)
                .SortByDescending(x => x.DispatchedAt)
                .FirstOrDefaultAsync(cancellationToken);

            return latest?.DispatchedAt;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get last alert time for sprintId={SprintId}", sprintId);
            return null;
        }
    }
}
