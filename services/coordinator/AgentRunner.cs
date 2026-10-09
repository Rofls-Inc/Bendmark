using System.Runtime.CompilerServices;
using Grpc.Core;
using Microsoft.Extensions.Options;
using Proto = Bendmark.Contracts.V1;

namespace Bendmark.Coordinator;

// Точка подмены агента: в приложении — GrpcAgentRunner, в тестах — фейк
public interface IAgentRunner
{
    // Один вызов на весь сценарий. Возвращает полные ступени по мере готовности.
    // Ошибки агента приходят как RpcException с кодом из контракта (proto/README.md)
    IAsyncEnumerable<StepResultDto> RunAsync(Guid runId, Scenario scenario, CancellationToken cancellationToken);

    // true, если агент нашёл и остановил идущий прогон с этим id
    Task<bool> StopAsync(Guid runId, CancellationToken cancellationToken);
}

public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    // Адрес gRPC-сервера агента (h2c)
    public string Address { get; set; } = "http://agent:50051";

    // Таймаут одного HTTP-запроса агента. Нужен, чтобы оценить срок вызова Run:
    // последние запросы ступени могут ждать ответа до этого таймаута
    public double RequestTimeoutSeconds { get; set; } = 5;

    public static readonly TimeSpan RunDeadlineMargin = TimeSpan.FromSeconds(30);

    // Сумма длительностей + ступени × таймаут запроса + 30 с запаса
    public TimeSpan RunCallTimeout(Scenario scenario)
    {
        var steps = scenario.Steps ?? [];
        var seconds = steps.Sum(step => step.DurationSeconds) + steps.Count * RequestTimeoutSeconds;
        return TimeSpan.FromSeconds(seconds) + RunDeadlineMargin;
    }

    // Stop отвечает после прекращения нагрузки: агент ждёт незавершённые запросы
    // не дольше их таймаута, плюс 5 с запаса на сеть
    public TimeSpan StopCallTimeout => TimeSpan.FromSeconds(RequestTimeoutSeconds + 5);
}

public sealed class GrpcAgentRunner : IAgentRunner
{
    private readonly Proto.AgentService.AgentServiceClient _client;
    private readonly AgentOptions _options;

    public GrpcAgentRunner(ChannelBase channel, IOptions<AgentOptions> options)
    {
        _client = new Proto.AgentService.AgentServiceClient(channel);
        _options = options.Value;
    }

    public async IAsyncEnumerable<StepResultDto> RunAsync(
        Guid runId,
        Scenario scenario,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var request = new Proto.RunRequest { RunId = runId.ToString(), Scenario = ToProto(scenario) };
        var deadline = DateTime.UtcNow + _options.RunCallTimeout(scenario);

        // Dispose вызова отменяет его, если перечисление прервали раньше конца потока
        using var call = _client.Run(request, deadline: deadline, cancellationToken: cancellationToken);
        await foreach (var response in call.ResponseStream.ReadAllAsync(cancellationToken))
            yield return ToDto(response.StepResult);
    }

    public async Task<bool> StopAsync(Guid runId, CancellationToken cancellationToken)
    {
        var response = await _client.StopAsync(
            new Proto.StopRequest { RunId = runId.ToString() },
            deadline: DateTime.UtcNow + _options.StopCallTimeout,
            cancellationToken: cancellationToken);
        return response.WasRunning;
    }

    private static Proto.Scenario ToProto(Scenario scenario)
    {
        var result = new Proto.Scenario
        {
            Name = scenario.Name ?? "",
            Target = new Proto.Target
            {
                Url = scenario.Target?.Url ?? "",
                Method = scenario.Target?.Method ?? "",
            },
        };
        foreach (var step in scenario.Steps ?? [])
            result.Steps.Add(new Proto.Step { TargetRps = step.TargetRps, DurationSeconds = step.DurationSeconds });
        return result;
    }

    private static StepResultDto ToDto(Proto.StepResult? step)
    {
        if (step is null)
            throw new AgentProtocolException("Агент прислал ответ без step_result");

        var latency = step.LatencyMs;
        var values = new[]
        {
            step.TargetRps, step.DurationSeconds, step.ThroughputRps,
            latency?.P50 ?? 0, latency?.P90 ?? 0, latency?.P99 ?? 0,
        };
        // NaN и бесконечность не сериализуются в JSON и ломали бы GET /runs/{id}
        if (!values.All(double.IsFinite))
            throw new AgentProtocolException($"Агент прислал нечисловые метрики в ступени {step.Index}");

        return StepResultDto.Create(
            step.Index,
            step.TargetRps,
            step.DurationSeconds,
            step.RequestCount,
            step.ThroughputRps,
            new LatencyDto { P50 = latency?.P50 ?? 0, P90 = latency?.P90 ?? 0, P99 = latency?.P99 ?? 0 },
            step.Errors?.Count ?? 0,
            step.SkippedCount);
    }
}

// Агент нарушил контракт (например, прислал ступени не по порядку)
public sealed class AgentProtocolException(string message) : Exception(message);
