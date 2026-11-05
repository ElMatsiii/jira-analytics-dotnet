using JiraAi.Analytics.Domain.Repositories;
using JiraAi.Analytics.Domain.Services;
using JiraAi.Analytics.Infrastructure.Grpc;
using JiraAi.Analytics.Infrastructure.Messaging;
using JiraAi.Analytics.Infrastructure.Persistence;
using JiraAi.Analytics.Worker.Workers;
using JiraAi.Contracts.Sprints.V1;

// 1. Enable plaintext HTTP/2 for gRPC client in .NET
AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

var builder = WebApplication.CreateBuilder(args);

// 2. Logging
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Information);

// 3. Configuration Options
builder.Services.Configure<AlertClientOptions>(
    builder.Configuration.GetSection(AlertClientOptions.SectionName));
builder.Services.Configure<KafkaOptions>(
    builder.Configuration.GetSection(KafkaOptions.SectionName));
builder.Services.Configure<MongoDbOptions>(
    builder.Configuration.GetSection(MongoDbOptions.SectionName));

// 4. Domain Calculators & Engines
builder.Services.AddSingleton<IBurndownCalculator, BurndownCalculator>();
builder.Services.AddSingleton<IVelocityCalculator, VelocityCalculator>();
builder.Services.AddSingleton<IHealthScoreCalculator, HealthScoreCalculator>();
builder.Services.AddSingleton<IRiskDetectionEngine, RiskDetectionEngine>();

// 5. MongoDB Persistence
builder.Services.AddSingleton<IMongoDbContext, MongoDbContext>();
builder.Services.AddSingleton<ISnapshotRepository, MongoSnapshotRepository>();
builder.Services.AddSingleton<IVelocityRepository, MongoVelocityRepository>();
builder.Services.AddSingleton<IAlertRepository, MongoAlertRepository>();

// 6. MS2 gRPC AlertService Client
var alertEndpoint = Environment.GetEnvironmentVariable("MS2_GRPC_URL")
    ?? builder.Configuration["AlertService:Endpoint"]
    ?? "http://localhost:50052";

builder.Services.AddGrpcClient<AlertService.AlertServiceClient>(options =>
{
    options.Address = new Uri(alertEndpoint);
})
.ConfigureChannel(channelOptions =>
{
    channelOptions.HttpHandler = new SocketsHttpHandler
    {
        EnableMultipleHttp2Connections = true,
        KeepAlivePingDelay = TimeSpan.FromSeconds(30),
        KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1)
    };
});

builder.Services.AddSingleton<IAlertClientService, AlertClientService>();

// 7. Kafka Messaging Processors & Producer
builder.Services.AddSingleton<IKafkaProducerWrapper, KafkaProducerWrapper>();
builder.Services.AddSingleton<IIssuesEventProcessor, IssuesEventProcessor>();
builder.Services.AddSingleton<IAnalyticsQueryProcessor, AnalyticsQueryProcessor>();

// 8. Hosted Background Workers
builder.Services.AddHostedService<IssuesEventsWorker>();
builder.Services.AddHostedService<AnalyticsQueryResponderWorker>();

var app = builder.Build();

// 9. Health & Readiness Endpoints for K8s / Docker probes
app.MapGet("/healthz", () => Results.Ok(new
{
    status = "healthy",
    service = "jira-analytics-dotnet",
    timestamp = DateTime.UtcNow
}));

app.MapGet("/ready", () => Results.Ok(new
{
    status = "ready"
}));

// Initialize MongoDB indexes asynchronously in background
_ = Task.Run(async () =>
{
    try
    {
        using var scope = app.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<IMongoDbContext>();
        await dbContext.InitializeIndexesAsync();
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Background index initialization deferred.");
    }
});

await app.RunAsync();
