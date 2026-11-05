namespace JiraAi.Analytics.Infrastructure.Persistence;

using JiraAi.Analytics.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

public class MongoDbOptions
{
    public const string SectionName = "Mongo";
    public string ConnectionString { get; set; } = "mongodb://localhost:27017";
    public string DatabaseName { get; set; } = "analytics_db";
    public string SnapshotsCollection { get; set; } = "sprint_snapshots";
    public string VelocityCollection { get; set; } = "velocity_history";
    public string AlertsCollection { get; set; } = "alerts";
}

public interface IMongoDbContext
{
    IMongoDatabase Database { get; }
    IMongoCollection<SprintSnapshotDocument> Snapshots { get; }
    IMongoCollection<VelocityRecordDocument> Velocity { get; }
    IMongoCollection<SprintAlertDocument> Alerts { get; }
    Task InitializeIndexesAsync(CancellationToken cancellationToken = default);
}

public class MongoDbContext : IMongoDbContext
{
    private readonly IMongoDatabase _database;
    private readonly MongoDbOptions _options;
    private readonly ILogger<MongoDbContext> _logger;

    public MongoDbContext(IOptions<MongoDbOptions> options, ILogger<MongoDbContext> logger)
    {
        _options = options.Value;
        _logger = logger;

        var connectionString = Environment.GetEnvironmentVariable("MONGODB_URI") 
            ?? _options.ConnectionString;
        var databaseName = Environment.GetEnvironmentVariable("MONGODB_DATABASE") 
            ?? _options.DatabaseName;

        var settings = MongoClientSettings.FromConnectionString(connectionString);
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(5);
        settings.RetryWrites = true;
        settings.RetryReads = true;

        var client = new MongoClient(settings);
        _database = client.GetDatabase(databaseName);
    }

    public MongoDbContext(IMongoDatabase database, IOptions<MongoDbOptions> options, ILogger<MongoDbContext> logger)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _options = options.Value;
        _logger = logger;
    }

    public IMongoDatabase Database => _database;

    public IMongoCollection<SprintSnapshotDocument> Snapshots =>
        _database.GetCollection<SprintSnapshotDocument>(_options.SnapshotsCollection);

    public IMongoCollection<VelocityRecordDocument> Velocity =>
        _database.GetCollection<VelocityRecordDocument>(_options.VelocityCollection);

    public IMongoCollection<SprintAlertDocument> Alerts =>
        _database.GetCollection<SprintAlertDocument>(_options.AlertsCollection);

    public async Task InitializeIndexesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // 1. sprint_snapshots indexes: unique on sprintId (T1-F20-05)
            var snapshotIndexKeys = Builders<SprintSnapshotDocument>.IndexKeys.Ascending(x => x.SprintId);
            var snapshotIndexOptions = new CreateIndexOptions { Unique = true };
            await Snapshots.Indexes.CreateOneAsync(
                new CreateIndexModel<SprintSnapshotDocument>(snapshotIndexKeys, snapshotIndexOptions),
                cancellationToken: cancellationToken);

            // 2. velocity_history indexes: compound on projectId + sprintId
            var velocityIndexKeys = Builders<VelocityRecordDocument>.IndexKeys
                .Ascending(x => x.ProjectId)
                .Ascending(x => x.SprintId);
            await Velocity.Indexes.CreateOneAsync(
                new CreateIndexModel<VelocityRecordDocument>(velocityIndexKeys, new CreateIndexOptions { Unique = true }),
                cancellationToken: cancellationToken);

            // 3. alerts indexes: sprintId + dispatchedAt
            var alertIndexKeys = Builders<SprintAlertDocument>.IndexKeys
                .Ascending(x => x.SprintId)
                .Descending(x => x.DispatchedAt);
            await Alerts.Indexes.CreateOneAsync(
                new CreateIndexModel<SprintAlertDocument>(alertIndexKeys),
                cancellationToken: cancellationToken);

            _logger.LogInformation("MongoDB indexes initialized successfully for analytics_db collections.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not initialize MongoDB indexes (DB may be offline during startup). Will retry on demand.");
        }
    }
}
