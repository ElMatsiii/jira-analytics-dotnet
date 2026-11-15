namespace JiraAi.Analytics.Infrastructure.Grpc;

using global::Grpc.Core;
using JiraAi.Analytics.Domain.Models;
using JiraAi.Analytics.Domain.Repositories;
using JiraAi.Contracts.Sprints.V1;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public class SprintDirectoryOptions
{
    public const string SectionName = "SprintService";
    public int TimeoutMs { get; set; } = 2000;
}

/// <summary>
/// MS3 → MS2 over gRPC (MS2's protocol). Reads a sprint with SprintService.GetSprint and converts
/// the proto message to MS3's own <see cref="SprintInfo"/> (interoperability rule).
/// </summary>
public class SprintDirectoryClient : ISprintDirectory
{
    private readonly SprintService.SprintServiceClient _client;
    private readonly SprintDirectoryOptions _options;
    private readonly ILogger<SprintDirectoryClient> _logger;

    public SprintDirectoryClient(
        SprintService.SprintServiceClient client,
        IOptions<SprintDirectoryOptions> options,
        ILogger<SprintDirectoryClient> logger)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _options = options?.Value ?? new SprintDirectoryOptions();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<SprintInfo?> GetSprintAsync(string sprintId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sprintId))
        {
            return null;
        }

        try
        {
            var callOptions = new CallOptions(
                deadline: DateTime.UtcNow.AddMilliseconds(_options.TimeoutMs),
                cancellationToken: cancellationToken);
            var response = await _client.GetSprintAsync(new GetSprintRequest { Id = sprintId }, callOptions);
            return response?.Sprint == null ? null : ToSprintInfo(response.Sprint);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            _logger.LogInformation("Sprint {SprintId} does not exist in MS2.", sprintId);
            return null;
        }
        catch (RpcException ex)
        {
            _logger.LogWarning(
                "MS2 SprintService unavailable ({StatusCode}) reading sprint {SprintId}: {Detail}. Keeping current snapshot dates.",
                ex.StatusCode, sprintId, ex.Status.Detail);
            return null;
        }
    }

    /// <summary>Converts MS2's proto Sprint (epoch milliseconds, proto enum) to MS3's model.</summary>
    public static SprintInfo ToSprintInfo(Sprint sprint)
    {
        ArgumentNullException.ThrowIfNull(sprint);
        var start = FromEpochMilliseconds(sprint.StartDate) ?? DateTime.UtcNow;
        var end = FromEpochMilliseconds(sprint.EndDate);
        if (end == null || end <= start)
        {
            end = start.AddDays(Domain.Services.SprintSnapshotRules.FallbackSprintDays);
        }

        return new SprintInfo(
            sprint.Id,
            sprint.ProjectId,
            sprint.Name,
            start,
            end.Value,
            ToLifecycle(sprint.Status));
    }

    public static SprintLifecycleStatus ToLifecycle(SprintStatus status) => status switch
    {
        SprintStatus.Planned => SprintLifecycleStatus.Planned,
        SprintStatus.Active => SprintLifecycleStatus.Active,
        SprintStatus.Completed => SprintLifecycleStatus.Completed,
        SprintStatus.Cancelled => SprintLifecycleStatus.Cancelled,
        _ => SprintLifecycleStatus.Unknown
    };

    /// <summary>MS2 sends dates as epoch milliseconds; 0 means "no date".</summary>
    public static DateTime? FromEpochMilliseconds(long value) =>
        value > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(value).UtcDateTime : null;
}
