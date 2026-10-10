using System.Text.Json.Serialization;

namespace Bendmark.Coordinator;

public sealed record LatencyDto
{
    [JsonPropertyName("p50")] public required double P50 { get; init; }
    [JsonPropertyName("p90")] public required double P90 { get; init; }
    [JsonPropertyName("p99")] public required double P99 { get; init; }
}

public sealed record ErrorsDto
{
    [JsonPropertyName("count")] public required ulong Count { get; init; }
    [JsonPropertyName("rate_percent")] public required double RatePercent { get; init; }
}

public sealed record StepResultDto
{
    [JsonPropertyName("index")] public required uint Index { get; init; }
    [JsonPropertyName("target_rps")] public required double TargetRps { get; init; }
    [JsonPropertyName("duration_seconds")] public required double DurationSeconds { get; init; }
    [JsonPropertyName("request_count")] public required ulong RequestCount { get; init; }
    [JsonPropertyName("throughput_rps")] public required double ThroughputRps { get; init; }
    [JsonPropertyName("latency_ms")] public required LatencyDto LatencyMs { get; init; }
    [JsonPropertyName("errors")] public required ErrorsDto Errors { get; init; }
    [JsonPropertyName("skipped_count")] public required ulong SkippedCount { get; init; }

    // rate_percent считается из счётчиков, а не берётся у агента: так же делает proto/result_json.py
    public static StepResultDto Create(
        uint index,
        double targetRps,
        double durationSeconds,
        ulong requestCount,
        double throughputRps,
        LatencyDto latencyMs,
        ulong errorCount,
        ulong skippedCount) => new()
    {
        Index = index,
        TargetRps = targetRps,
        DurationSeconds = durationSeconds,
        RequestCount = requestCount,
        ThroughputRps = throughputRps,
        LatencyMs = latencyMs,
        Errors = new ErrorsDto { Count = errorCount, RatePercent = RatePercent(errorCount, requestCount) },
        SkippedCount = skippedCount,
    };

    // Доля ошибок в процентах, один знак после запятой; при нуле запросов - 0
    public static double RatePercent(ulong errorCount, ulong requestCount) =>
        requestCount == 0 ? 0 : Math.Round(100.0 * errorCount / requestCount, 1);
}

// Обёртка result.json: {scenario_name, status, steps}
public sealed record RunResultDto
{
    [JsonPropertyName("scenario_name")] public required string ScenarioName { get; init; }
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("steps")] public required IReadOnlyList<StepResultDto> Steps { get; init; }

    public static bool CanExport(Run run) => run.Status is RunStatus.Completed or RunStatus.Aborted;

    public static RunResultDto From(Run run)
    {
        if (!CanExport(run))
            throw new InvalidOperationException("result.json есть только у завершённого или остановленного прогона");

        return new RunResultDto
        {
            ScenarioName = run.Scenario.Name ?? "",
            Status = run.Status == RunStatus.Completed ? "completed" : "aborted",
            Steps = run.Steps,
        };
    }
}
