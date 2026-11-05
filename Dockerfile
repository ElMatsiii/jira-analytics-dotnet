# ==============================================================================
# MS3 (jira-analytics-dotnet) Multi-Stage Dockerfile
# Stage 1: Build & Publish .NET 8 Worker
# Stage 2: Minimal, Non-Root .NET 8 Runtime (Debian / glibc for Confluent.Kafka)
# ==============================================================================

# ------------------------------------------------------------------------------
# Stage 1: Builder
# ------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS builder

WORKDIR /src

# Copy all source files
COPY . .

# Restore dependencies
RUN dotnet restore jira-analytics-dotnet.sln

# Build and publish release binaries
RUN dotnet publish src/JiraAi.Analytics.Worker/JiraAi.Analytics.Worker.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

# ------------------------------------------------------------------------------
# Stage 2: Production Runtime
# ------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runner

WORKDIR /app

ENV DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:8080 \
    HEALTH_PORT=8080 \
    MONGODB_URI=mongodb://mongo-analytics:27017 \
    MONGODB_DATABASE=analytics_db \
    KAFKA_BOOTSTRAP_SERVERS=kafka:9092 \
    KAFKA_TOPIC_ISSUES=issues.events \
    KAFKA_TOPIC_QUERY=analytics.query \
    KAFKA_TOPIC_REPLY=analytics.reply \
    MS2_GRPC_URL=http://jira-sprints-nestjs:50052

# Copy compiled application with non-root user permissions
COPY --from=builder --chown=app:app /app/publish /app

# Switch to built-in non-root user 'app' (UID 1654 in .NET 8)
USER app

EXPOSE 8080

ENTRYPOINT ["dotnet", "JiraAi.Analytics.Worker.dll"]
