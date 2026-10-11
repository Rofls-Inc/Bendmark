using Grpc.Core;
using Microsoft.Extensions.Options;
using Proto = Bendmark.Contracts.V1;

namespace Bendmark.Coordinator;

// Точка подмены анализатора: в приложении — GrpcAnalyzer, в тестах — фейк.
// Ошибки анализатора приходят как RpcException с кодом из контракта
public interface IAnalyzer
{
    Task<LimitResult> FindLimitAsync(IReadOnlyList<StepResultDto> steps, CancellationToken cancellationToken);
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
    private readonly Proto.AnalyzerService.AnalyzerServiceClient _client;
    private readonly AnalyzerOptions _options;

    public GrpcAnalyzer(ChannelBase channel, IOptions<AnalyzerOptions> options)
    {
        _client = new Proto.AnalyzerService.AnalyzerServiceClient(channel);
        _options = options.Value;
    }

    public async Task<LimitResult> FindLimitAsync(IReadOnlyList<StepResultDto> steps, CancellationToken cancellationToken)
    {
        // options не передаём: анализатор берёт пороги по умолчанию
        var request = new Proto.FindLimitRequest();
        request.Steps.Add(steps.Select(ToProto));

        var response = await _client.FindLimitAsync(
            request,
            deadline: DateTime.UtcNow.AddSeconds(_options.TimeoutSeconds),
            cancellationToken: cancellationToken);

        var limit = response.Limit ?? throw new InvalidOperationException("Анализатор прислал ответ без limit");
        return new LimitResult(
            limit.Found,
            limit.HasLimitRps ? limit.LimitRps : null,
            limit.HasFailedStepIndex ? limit.FailedStepIndex : null,
            limit.Reasons.ToList());
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
