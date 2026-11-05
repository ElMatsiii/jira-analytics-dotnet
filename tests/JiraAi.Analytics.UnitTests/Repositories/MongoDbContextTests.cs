namespace JiraAi.Analytics.UnitTests.Repositories;

using FluentAssertions;
using JiraAi.Analytics.Domain.Models;
using JiraAi.Analytics.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using NSubstitute;
using Xunit;

public class MongoDbContextTests
{
    [Fact]
    public void MongoDbContext_Properties_ReturnCollectionsFromDatabase()
    {
        var mockDb = Substitute.For<IMongoDatabase>();
        var mockSnapshots = Substitute.For<IMongoCollection<SprintSnapshotDocument>>();
        var mockVelocity = Substitute.For<IMongoCollection<VelocityRecordDocument>>();
        var mockAlerts = Substitute.For<IMongoCollection<SprintAlertDocument>>();

        mockDb.GetCollection<SprintSnapshotDocument>("sprint_snapshots").Returns(mockSnapshots);
        mockDb.GetCollection<VelocityRecordDocument>("velocity_history").Returns(mockVelocity);
        mockDb.GetCollection<SprintAlertDocument>("alerts").Returns(mockAlerts);

        var options = Options.Create(new MongoDbOptions
        {
            DatabaseName = "analytics_db",
            SnapshotsCollection = "sprint_snapshots",
            VelocityCollection = "velocity_history",
            AlertsCollection = "alerts"
        });

        var context = new MongoDbContext(mockDb, options, NullLogger<MongoDbContext>.Instance);

        context.Database.Should().BeSameAs(mockDb);
        context.Snapshots.Should().BeSameAs(mockSnapshots);
        context.Velocity.Should().BeSameAs(mockVelocity);
        context.Alerts.Should().BeSameAs(mockAlerts);
    }

    [Fact]
    public async Task MongoDbContext_InitializeIndexesAsync_HandlesExceptionsGracefully()
    {
        var mockDb = Substitute.For<IMongoDatabase>();
        var mockSnapshots = Substitute.For<IMongoCollection<SprintSnapshotDocument>>();
        mockDb.GetCollection<SprintSnapshotDocument>("sprint_snapshots").Returns(mockSnapshots);

        // Simulate database exception during index creation
        mockSnapshots.Indexes.When(x => x.CreateOneAsync(Arg.Any<CreateIndexModel<SprintSnapshotDocument>>(), Arg.Any<CreateOneIndexOptions>(), Arg.Any<CancellationToken>()))
            .Do(_ => throw new MongoException("Connection failure"));

        var options = Options.Create(new MongoDbOptions());
        var context = new MongoDbContext(mockDb, options, NullLogger<MongoDbContext>.Instance);

        // Act & Assert: does not throw unhandled exception
        await context.InitializeIndexesAsync();
    }
}
