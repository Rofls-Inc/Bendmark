using Grpc.Net.Client;
using Microsoft.Extensions.Options;

namespace Bendmark.Coordinator;

// Один агент группы. Number - с единицы, в порядке Agent:Addresses
public sealed record AgentEndpoint(int Number, string Address, IAgentRunner Runner);

// Агенты, на которых идёт каждый прогон (#78). Нагрузка сценария делится между ними поровну,
// ступени складываются в одну (StepMerger). При одном агенте всё как до #78
public sealed class AgentGroup : IDisposable
{
    private readonly IReadOnlyList<IDisposable> _owned;

    public AgentGroup(IEnumerable<AgentEndpoint> agents, IReadOnlyList<IDisposable>? owned = null)
    {
        Agents = agents.ToList();
        if (Agents.Count == 0)
            throw new ArgumentException("Нужен хотя бы один агент", nameof(agents));
        _owned = owned ?? [];
    }

    public IReadOnlyList<AgentEndpoint> Agents { get; }

    public int Count => Agents.Count;

    // gRPC-клиенты по адресам из настроек; каналы закрываются вместе с группой
    public static AgentGroup Connect(IOptions<AgentOptions> options)
    {
        var addresses = options.Value.ResolveAddresses();
        var channels = addresses.Select(address => GrpcChannel.ForAddress(address)).ToList();
        var agents = addresses.Select((address, i) =>
            new AgentEndpoint(i + 1, address, new GrpcAgentRunner(channels[i], options)));
        return new AgentGroup(agents, channels);
    }

    // Для ошибок и логов: «Агент 2 из 3 (http://agent-2:50051)»
    public string Describe(AgentEndpoint agent) => $"Агент {agent.Number} из {Count} ({agent.Address})";

    // Ошибка прогона с указанием агента. При одном агенте текст как до #78
    public string WithAgent(AgentEndpoint agent, string error) => Count == 1 ? error : $"{Describe(agent)}: {error}";

    public void Dispose()
    {
        foreach (var resource in _owned)
            resource.Dispose();
    }
}

public static class ScenarioSplit
{
    // Сценарий для одного из agentCount агентов: те же ступени с долей нагрузки
    public static Scenario ForAgent(Scenario scenario, int agentCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(agentCount, 1);
        if (agentCount == 1)
            return scenario;

        return scenario with
        {
            Steps = scenario.Steps?.Select(step => step with { TargetRps = step.TargetRps / agentCount }).ToList(),
        };
    }
}

public static class StepMerger
{
    // Ступень прогона из одноимённых ступеней всех агентов.
    // Счётчики складываются, пропускная и доля ошибок считаются из сумм,
    // нагрузка и длительность берутся из сценария. Перцентили - максимум по агентам с запросами:
    // агенты отдают только p50/p90/p99, а общий перцентиль лежит между минимумом и
    // максимумом перцентилей агентов, так что максимум - оценка сверху
    public static StepResultDto Merge(uint index, Step planned, IReadOnlyList<StepResultDto> parts)
    {
        if (parts.Count == 0)
            throw new ArgumentException("Нужна хотя бы одна ступень агента", nameof(parts));
        // Один агент: ступень как прислал агент, без пересчёта
        if (parts.Count == 1)
            return parts[0];

        ulong requests = 0, errors = 0, skipped = 0;
        foreach (var part in parts)
        {
            requests = checked(requests + part.RequestCount);
            errors = checked(errors + part.Errors.Count);
            skipped = checked(skipped + part.SkippedCount);
        }

        // Перцентили агента без запросов - нули, это отсутствие измерений, а не нулевая задержка
        var measured = parts.Where(part => part.RequestCount > 0).Select(part => part.LatencyMs).ToList();
        var latency = measured.Count == 0
            ? new LatencyDto { P50 = 0, P90 = 0, P99 = 0 }
            : new LatencyDto
            {
                P50 = measured.Max(l => l.P50),
                P90 = measured.Max(l => l.P90),
                P99 = measured.Max(l => l.P99),
            };

        return StepResultDto.Create(
            index,
            planned.TargetRps,
            planned.DurationSeconds,
            requests,
            requests / planned.DurationSeconds,
            latency,
            errors,
            skipped);
    }
}
