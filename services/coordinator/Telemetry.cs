using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Bendmark.Coordinator;

public sealed class TelemetryOptions
{
    public const string SectionName = "Telemetry";

    public string? Url { get; set; }
    public double IntervalSeconds { get; set; } = 1;
    public double TimeoutSeconds { get; set; } = 0.5;
}

public sealed record TelemetrySnapshot(
    int SchemaVersion,
    DateTime CapturedAt,
    DateTime StartedAt,
    long? WorkersBusy,
    long? WorkersTotal,
    long? QueueLength,
    long? QueueCapacity,
    long? CompletedTotal,
    long? RejectedTotal)
{
    internal bool IsValid() =>
        SchemaVersion == 1 &&
        CapturedAt.Kind == DateTimeKind.Utc &&
        StartedAt.Kind == DateTimeKind.Utc &&
        StartedAt <= CapturedAt &&
        WorkersBusy is null or >= 0 &&
        WorkersTotal is null or >= 0 &&
        QueueLength is null or >= 0 &&
        QueueCapacity is null or >= 0 &&
        CompletedTotal is null or >= 0 &&
        RejectedTotal is null or >= 0 &&
        !(WorkersBusy > WorkersTotal) &&
        !(QueueLength > QueueCapacity);
}

public sealed record TelemetrySample(Guid RunId, DateTime ObservedAt, TelemetrySnapshot Snapshot);

public enum TelemetryStatus
{
    Disabled,
    Unavailable,
    Partial,
    Complete,
}

public sealed record RunTelemetry(
    TelemetryStatus Status,
    int FailedPolls,
    IReadOnlyList<TelemetrySample> Snapshots)
{
    public static RunTelemetry Disabled { get; } = new(TelemetryStatus.Disabled, 0, []);
}

public sealed class TelemetryCollector(
    IHttpClientFactory clients,
    IOptions<TelemetryOptions> options,
    RunStore store,
    TimeProvider time,
    ILogger<TelemetryCollector> logger)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly TelemetryOptions _options = options.Value;

    public bool Enabled => !string.IsNullOrWhiteSpace(_options.Url);

    public async Task CollectAsync(Guid runId, CancellationToken cancellationToken)
    {
        if (!Enabled)
            return;

        using var client = clients.CreateClient(TelemetryOptions.SectionName);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var snapshot = await PollAsync(client, runId, cancellationToken);
                store.AddTelemetryPoll(runId, time.GetUtcNow().UtcDateTime, snapshot);
                await Task.Delay(TimeSpan.FromSeconds(_options.IntervalSeconds), time, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Отменённый при завершении прогона запрос не считается пропуском.
        }
    }

    private async Task<TelemetrySnapshot?> PollAsync(
        HttpClient client, Guid runId, CancellationToken cancellationToken)
    {
        try
        {
            // GetAsync буферизует тело в пределах лимита клиента. Тайм-аут охватывает и тело.
            using var response = await client.GetAsync(_options.Url, cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                logger.LogDebug("Источник телеметрии прогона {RunId} ответил {Status}", runId, response.StatusCode);
                return null;
            }

            var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            var snapshot = await JsonSerializer.DeserializeAsync<TelemetrySnapshot>(body, Json, cancellationToken);
            return snapshot is not null && snapshot.IsValid() ? snapshot : null;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException ||
                                  e is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            logger.LogDebug(e, "Не удалось опросить источник телеметрии прогона {RunId}", runId);
            return null;
        }
    }
}
