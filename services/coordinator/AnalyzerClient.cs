using Grpc.Core;
using Microsoft.Extensions.Options;
using Proto = Bendmark.Contracts.V1;

namespace Bendmark.Coordinator;

// Точка подмены анализатора: в приложении — GrpcAnalyzer, в тестах — фейк.
// Ошибки анализатора приходят как RpcException с кодом из контракта
public interface IAnalyzer
{
    Task<LimitResult> FindLimitAsync(
        IReadOnlyList<StepResultDto> steps, CancellationToken cancellationToken,
        IReadOnlyList<TelemetrySample>? snapshots = null);
}

public sealed class AnalyzerOptions
{
    public const string SectionName = "Analyzer";

    // Адрес gRPC-сервера анализатора (h2c)
    public string Address { get; set; } = "http://analyzer:50052";

    // Срок вызова FindLimit: поиск предела — простой проход по ступеням
    public double TimeoutSeconds { get; set; } = 10;
}

public sealed class GrpcAnalyzer : IAnalyzer
{
    // Стандартный предел размера сообщения gRPC на стороне анализатора.
    private const int MaxRequestBytes = 4 * 1024 * 1024;

    private readonly Proto.AnalyzerService.AnalyzerServiceClient _client;
    private readonly AnalyzerOptions _options;

    public GrpcAnalyzer(ChannelBase channel, IOptions<AnalyzerOptions> options)
    {
        _client = new Proto.AnalyzerService.AnalyzerServiceClient(channel);
        _options = options.Value;
    }

    public async Task<LimitResult> FindLimitAsync(
        IReadOnlyList<StepResultDto> steps, CancellationToken cancellationToken,
        IReadOnlyList<TelemetrySample>? snapshots = null)
    {
        // options не передаём: анализатор берёт пороги по умолчанию
        var request = new Proto.FindLimitRequest();
        request.Steps.Add(steps.Select(ToProto));
        if (snapshots is { Count: > 0 })
        {
            AddTelemetry(request, steps, snapshots);
            if (request.CalculateSize() > MaxRequestBytes)
                request.Telemetry.Clear();
        }

        var response = await _client.FindLimitAsync(
            request,
            deadline: DateTime.UtcNow.AddSeconds(_options.TimeoutSeconds),
            cancellationToken: cancellationToken);

        var limit = response.Limit ?? throw new InvalidOperationException("Анализатор прислал ответ без limit");
        return new LimitResult(
            limit.Found,
            limit.HasLimitRps ? limit.LimitRps : null,
            limit.HasFailedStepIndex ? limit.FailedStepIndex : null,
            limit.Reasons.ToList(),
            limit.Confirmed,
            ToDiagnosis(response.Diagnosis));
    }

    // Без диагноза или со статусом UNSPECIFIED предел всё равно
    // показываем: диагноз - дополнение к нему, а не условие анализа
    private static Diagnosis? ToDiagnosis(Proto.Diagnosis? diagnosis)
    {
        DiagnosisStatus? status = diagnosis?.Status switch
        {
            Proto.DiagnosisStatus.Diagnosed => DiagnosisStatus.Diagnosed,
            Proto.DiagnosisStatus.NoLimit => DiagnosisStatus.NoLimit,
            Proto.DiagnosisStatus.TooFewSteps => DiagnosisStatus.TooFewSteps,
            _ => null,
        };
        if (diagnosis is null || status is null)
            return null;

        var diagnosed = status == DiagnosisStatus.Diagnosed;
        return new Diagnosis
        {
            Status = status.Value,
            Cause = diagnosed ? ToCause(diagnosis.Cause) : null,
            Confidence = diagnosed ? ToConfidence(diagnosis.Confidence) : null,
            Explanation = diagnosis.Explanation,
            Evidence = diagnosis.Evidence.ToList(),
        };
    }

    private static DiagnosisCause? ToCause(Proto.Cause cause) => cause switch
    {
        Proto.Cause.Rejecting => DiagnosisCause.Rejecting,
        Proto.Cause.SlowResponses => DiagnosisCause.SlowResponses,
        Proto.Cause.Saturation => DiagnosisCause.Saturation,
        Proto.Cause.Inconclusive => DiagnosisCause.Inconclusive,
        _ => null,
    };

    private static DiagnosisConfidence? ToConfidence(Proto.Confidence confidence) => confidence switch
    {
        Proto.Confidence.Low => DiagnosisConfidence.Low,
        Proto.Confidence.Medium => DiagnosisConfidence.Medium,
        Proto.Confidence.High => DiagnosisConfidence.High,
        _ => null,
    };

    private static void AddTelemetry(
        Proto.FindLimitRequest request, IReadOnlyList<StepResultDto> steps, IReadOnlyList<TelemetrySample> snapshots)
    {
        var samples = snapshots.Select(sample => ToProto(sample.Snapshot)).ToArray();
        foreach (var step in steps)
        {
            if (step.LoadStartedUnixUs is not { } start || step.LoadEndedUnixUs is not { } end)
                continue;

            var telemetry = new Proto.StepTelemetry { StepIndex = step.Index };
            telemetry.Snapshots.Add(samples.Where(sample => sample.CapturedUnixUs >= start && sample.CapturedUnixUs < end));
            if (telemetry.Snapshots.Count >= 2)
                request.Telemetry.Add(telemetry);
        }
    }

    private static Proto.TargetSnapshot ToProto(TelemetrySnapshot snapshot)
    {
        var result = new Proto.TargetSnapshot
        {
            CapturedUnixUs = (snapshot.CapturedAt.Ticks - DateTime.UnixEpoch.Ticks) / 10,
            StartedUnixUs = (snapshot.StartedAt.Ticks - DateTime.UnixEpoch.Ticks) / 10,
        };
        if (snapshot.WorkersBusy is { } busy) result.WorkersBusy = busy;
        if (snapshot.WorkersTotal is { } total) result.WorkersTotal = total;
        if (snapshot.QueueLength is { } length) result.QueueLength = length;
        if (snapshot.QueueCapacity is { } capacity) result.QueueCapacity = capacity;
        if (snapshot.CompletedTotal is { } completed) result.CompletedTotal = completed;
        return result;
    }

    private static Proto.StepResult ToProto(StepResultDto step) => new()
    {
        Index = step.Index,
        TargetRps = step.TargetRps,
        DurationSeconds = step.DurationSeconds,
        RequestCount = step.RequestCount,
        ThroughputRps = step.ThroughputRps,
        LatencyMs = new Proto.LatencyPercentiles
        {
            P50 = step.LatencyMs.P50,
            P90 = step.LatencyMs.P90,
            P99 = step.LatencyMs.P99,
        },
        Errors = new Proto.RequestErrors { Count = step.Errors.Count, RatePercent = step.Errors.RatePercent },
        SkippedCount = step.SkippedCount,
    };
}
