using System.Net;
using System.Text;
using System.Text.Json;
using Grpc.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bendmark.Coordinator.Tests;

// Деление нагрузки и сложение ступеней (#78), без хоста
public class MultiAgentUnitTests
{
    private static Scenario Ramp() => new()
    {
        Name = "ramp",
        Target = new Target { Url = "http://abstock:8080/", Method = "GET" },
        Steps =
        [
            new Step { TargetRps = 150, DurationSeconds = 10 },
            new Step { TargetRps = 300, DurationSeconds = 10 },
            new Step { TargetRps = 450, DurationSeconds = 12.5 },
        ],
    };

    private static StepResultDto Part(uint index, double targetRps, ulong requests, double p99,
        ulong errors = 0, ulong skipped = 0, double durationSeconds = 10) =>
        StepResultDto.Create(index, targetRps, durationSeconds, requests, requests / durationSeconds,
            new LatencyDto { P50 = p99 / 4, P90 = p99 / 2, P99 = p99 }, errors, skipped);

    [Fact]
    public void Each_agent_gets_an_equal_share_of_every_step()
    {
        var scenario = Ramp();

        var share = ScenarioSplit.ForAgent(scenario, 3);

        Assert.Equal(new[] { 50.0, 100.0, 150.0 }, share.Steps!.Select(step => step.TargetRps));
        Assert.Equal(new[] { 10.0, 10.0, 12.5 }, share.Steps!.Select(step => step.DurationSeconds));
        Assert.Equal(scenario.Name, share.Name);
        Assert.Equal(scenario.Target, share.Target);
        // Сценарий прогона не меняется
        Assert.Equal(new[] { 150.0, 300.0, 450.0 }, scenario.Steps!.Select(step => step.TargetRps));
    }

    [Fact]
    public void Single_agent_gets_the_scenario_as_is()
    {
        var scenario = Ramp();
        Assert.Same(scenario, ScenarioSplit.ForAgent(scenario, 1));
    }

    [Fact]
    public void Merge_sums_counters_and_takes_load_from_the_scenario()
    {
        var planned = new Step { TargetRps = 300, DurationSeconds = 10 };
        var merged = StepMerger.Merge(2, planned,
        [
            Part(2, 100, 1000, 20, errors: 3),
            Part(2, 100, 990, 35, errors: 7, skipped: 2),
            Part(2, 100, 1001, 25),
        ]);

        Assert.Equal(2u, merged.Index);
        Assert.Equal(300, merged.TargetRps);
        Assert.Equal(10, merged.DurationSeconds);
        Assert.Equal(2991UL, merged.RequestCount);
        Assert.Equal(299.1, merged.ThroughputRps, 9);
        Assert.Equal(10UL, merged.Errors.Count);
        // 100 × 10 / 2991 = 0.334 %
        Assert.Equal(0.3, merged.Errors.RatePercent);
        Assert.Equal(2UL, merged.SkippedCount);
    }

    [Fact]
    public void Merged_percentiles_are_the_maximum_over_agents()
    {
        var merged = StepMerger.Merge(1, new Step { TargetRps = 200, DurationSeconds = 10 },
        [
            StepResultDto.Create(1, 100, 10, 1000, 100, new LatencyDto { P50 = 10, P90 = 40, P99 = 90 }, 0, 0),
            StepResultDto.Create(1, 100, 10, 1000, 100, new LatencyDto { P50 = 12, P90 = 30, P99 = 120 }, 0, 0),
        ]);

        Assert.Equal(new LatencyDto { P50 = 12, P90 = 40, P99 = 120 }, merged.LatencyMs);
    }

    [Fact]
    public void Agent_without_requests_does_not_affect_percentiles()
    {
        var merged = StepMerger.Merge(1, new Step { TargetRps = 200, DurationSeconds = 10 },
        [
            Part(1, 100, 1000, 80),
            Part(1, 100, 0, 0, skipped: 1000),
        ]);

        Assert.Equal(80, merged.LatencyMs.P99);
        Assert.Equal(1000UL, merged.RequestCount);
        Assert.Equal(1000UL, merged.SkippedCount);
    }

    [Fact]
    public void Step_without_requests_on_all_agents_has_zero_percentiles()
    {
        var merged = StepMerger.Merge(1, new Step { TargetRps = 200, DurationSeconds = 10 },
            [Part(1, 100, 0, 0), Part(1, 100, 0, 0)]);

        Assert.Equal(new LatencyDto { P50 = 0, P90 = 0, P99 = 0 }, merged.LatencyMs);
        Assert.Equal(0, merged.ThroughputRps);
        Assert.Equal(0, merged.Errors.RatePercent);
    }

    [Fact]
    public void Single_agent_step_is_kept_as_sent()
    {
        var step = Part(1, 100, 1000, 20, errors: 1);
        Assert.Same(step, StepMerger.Merge(1, new Step { TargetRps = 100, DurationSeconds = 10 }, [step]));
    }

    [Fact]
    public void Addresses_replace_the_single_address()
    {
        Assert.Equal(new[] { "http://agent:50051" }, new AgentOptions().ResolveAddresses());

        var options = new AgentOptions { Addresses = ["http://agent-1:50051", "http://agent-2:50051"] };
        Assert.Equal(new[] { "http://agent-1:50051", "http://agent-2:50051" }, options.ResolveAddresses());
    }

    [Theory]
    [InlineData("http://agent-1:50051", "http://agent-2:50051", false)]
    [InlineData("http://agent-1:50051", "http://agent-1:50051", true)]
    [InlineData("http://agent-1:50051", "http://AGENT-1:50051/", true)]
    public void Duplicate_addresses_are_compared_as_urls(string first, string second, bool duplicate)
    {
        var options = new AgentOptions { Addresses = [first, second] };
        Assert.Equal(duplicate, options.HasDuplicateAddresses());
    }

    [Fact]
    public void Group_connects_to_every_address_in_order()
    {
        var options = Options.Create(new AgentOptions
        {
            Addresses = ["http://agent:50051", "http://agent-2:50051", "http://agent-3:50051"],
        });

        using var group = AgentGroup.Connect(options);

        Assert.Equal(3, group.Count);
        Assert.Equal(new[] { 1, 2, 3 }, group.Agents.Select(agent => agent.Number));
        Assert.Equal(options.Value.Addresses, group.Agents.Select(agent => agent.Address));
        Assert.All(group.Agents, agent => Assert.IsType<GrpcAgentRunner>(agent.Runner));
        Assert.Equal("Агент 2 из 3 (http://agent-2:50051)", group.Describe(group.Agents[1]));
        Assert.Equal("Агент 2 из 3 (http://agent-2:50051): сбой", group.WithAgent(group.Agents[1], "сбой"));
    }

    [Fact]
    public void Single_agent_errors_have_no_agent_prefix()
    {
        using var group = AgentGroup.Connect(Options.Create(new AgentOptions()));
        Assert.Equal("сбой", group.WithAgent(group.Agents[0], "сбой"));
    }
}

// Прогон на трёх фейковых агентах через API координатора (#78)
public class MultiAgentRunTests
{
    // Две ступени по 10 с: каждому из трёх агентов - 50 и 100 запр/с
    private const string Scenario = """
        {
          "name": "multi",
          "target": { "url": "http://abstock:8080/", "method": "GET" },
          "steps": [
            { "target_rps": 150, "duration_seconds": 10 },
            { "target_rps": 300, "duration_seconds": 10 }
          ]
        }
        """;

    private static async Task<Guid> CreateRunAsync(HttpClient client)
    {
        using var response = await client.PostAsync("/runs", new StringContent(Scenario, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await Api.ReadJsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static StepResultDto Part(uint index, ulong requests, double p99, ulong errors = 0)
    {
        var targetRps = index == 1 ? 50 : 100;
        return StepResultDto.Create(index, targetRps, 10, requests, requests / 10.0,
            new LatencyDto { P50 = p99 / 4, P90 = p99 / 2, P99 = p99 }, errors, 0);
    }

    private static async Task WaitAllStartedAsync(CoordinatorFactory factory, Guid id)
    {
        foreach (var agent in factory.Agents)
            Assert.Equal(id, await agent.WaitStartedAsync());
    }

    private static void SendStepOnAll(CoordinatorFactory factory, uint index)
    {
        foreach (var agent in factory.Agents)
            agent.SendStep(Part(index, index * 500, 20));
    }

    private static string Error(JsonElement run) => run.GetProperty("error").GetString()!;

    [Fact]
    public async Task Run_is_split_between_agents_and_steps_are_merged()
    {
        await using var factory = new CoordinatorFactory { AgentCount = 3 };
        var client = factory.CreateClient();

        var id = await CreateRunAsync(client);
        await WaitAllStartedAsync(factory, id);

        foreach (var agent in factory.Agents)
        {
            var share = Assert.Single(agent.Scenarios);
            Assert.Equal(new[] { 50.0, 100.0 }, share.Steps!.Select(step => step.TargetRps));
            Assert.Equal(new[] { 10.0, 10.0 }, share.Steps!.Select(step => step.DurationSeconds));
            Assert.Equal("http://abstock:8080/", share.Target!.Url);
        }

        var running = await Api.GetRunAsync(client, id);
        Assert.Equal(3, running.GetProperty("agent_count").GetInt32());
        // В прогоне - исходный сценарий, а не доли
        Assert.Equal(150, running.GetProperty("scenario").GetProperty("steps")[0].GetProperty("target_rps").GetDouble());

        // Ступень от двух агентов из трёх в прогон не попадает
        factory.Agents[0].SendStep(Part(1, 500, 20));
        factory.Agents[1].SendStep(Part(1, 490, 35, errors: 5));
        await Task.Delay(200);
        Assert.Equal(0, Api.StepCount(await Api.GetRunAsync(client, id)));

        factory.Agents[2].SendStep(Part(1, 480, 25));
        var withStep = await Api.WaitForAsync(client, id, run => Api.StepCount(run) == 1);
        var step = withStep.GetProperty("steps")[0];
        Assert.Equal(1, step.GetProperty("index").GetInt32());
        Assert.Equal(150, step.GetProperty("target_rps").GetDouble());
        Assert.Equal(10, step.GetProperty("duration_seconds").GetDouble());
        Assert.Equal(1470UL, step.GetProperty("request_count").GetUInt64());
        Assert.Equal(147, step.GetProperty("throughput_rps").GetDouble(), 9);
        Assert.Equal(35, step.GetProperty("latency_ms").GetProperty("p99").GetDouble());
        Assert.Equal(5UL, step.GetProperty("errors").GetProperty("count").GetUInt64());
        Assert.Equal(0.3, step.GetProperty("errors").GetProperty("rate_percent").GetDouble());

        SendStepOnAll(factory, 2);
        foreach (var agent in factory.Agents)
            agent.Finish();

        var done = await Api.WaitForStatusAsync(client, id, "completed");
        Assert.Equal(2, Api.StepCount(done));
        Assert.Equal(300, done.GetProperty("steps")[1].GetProperty("target_rps").GetDouble());
        Assert.Equal(3000UL, done.GetProperty("steps")[1].GetProperty("request_count").GetUInt64());
        Assert.Equal(3, done.GetProperty("agent_count").GetInt32());

        // Анализатор получает сложенные ступени
        await Api.WaitForAsync(client, id, run => run.GetProperty("analysis").GetProperty("status").GetString() != "pending");
        var analyzed = Assert.Single(factory.Analyzer.Calls);
        Assert.Equal(new[] { 150.0, 300.0 }, analyzed.Select(s => s.TargetRps));
    }

    [Fact]
    public async Task Single_agent_run_has_agent_count_one()
    {
        await using var factory = new CoordinatorFactory();
        var client = factory.CreateClient();

        var id = await CreateRunAsync(client);
        await factory.Agent.WaitStartedAsync();

        var run = await Api.GetRunAsync(client, id);
        Assert.Equal(1, run.GetProperty("agent_count").GetInt32());
        // Один агент получает сценарий целиком
        Assert.Equal(new[] { 150.0, 300.0 }, Assert.Single(factory.Agent.Scenarios).Steps!.Select(step => step.TargetRps));
    }

    [Fact]
    public async Task Agent_error_cancels_other_agents_and_fails_the_run()
    {
        await using var factory = new CoordinatorFactory { AgentCount = 3 };
        var client = factory.CreateClient();

        var id = await CreateRunAsync(client);
        await WaitAllStartedAsync(factory, id);
        SendStepOnAll(factory, 1);
        await Api.WaitForAsync(client, id, run => Api.StepCount(run) == 1);

        // Ступень 2 пришла только от первого агента: в прогон она не попадёт
        factory.Agents[0].SendStep(Part(2, 1000, 20));
        factory.Agents[1].Fail(StatusCode.Internal, "boom");

        var failed = await Api.WaitForStatusAsync(client, id, "failed");
        Assert.Equal($"Агент 2 из 3 ({CoordinatorFactory.AgentAddress(2)}): Внутренняя ошибка агента: boom", Error(failed));
        Assert.Equal(1, Api.StepCount(failed));
        // Остальным вызов отменён, Stop не нужен
        Assert.All(factory.Agents, agent => Assert.Empty(agent.StopCalls));
    }

    [Fact]
    public async Task Agent_protocol_violation_fails_the_run_with_agent_name()
    {
        await using var factory = new CoordinatorFactory { AgentCount = 3 };
        var client = factory.CreateClient();

        var id = await CreateRunAsync(client);
        await WaitAllStartedAsync(factory, id);
        factory.Agents[0].SendStep(Part(2, 1000, 20));

        var failed = await Api.WaitForStatusAsync(client, id, "failed");
        Assert.Equal(
            $"Агент 1 из 3 ({CoordinatorFactory.AgentAddress(1)}): Ошибка обмена с агентом: Агент прислал ступень 2, ожидалась 1",
            Error(failed));
        Assert.Equal(0, Api.StepCount(failed));
    }

    [Fact]
    public async Task Deadline_on_one_agent_aborts_the_run()
    {
        await using var factory = new CoordinatorFactory { AgentCount = 3 };
        var client = factory.CreateClient();

        var id = await CreateRunAsync(client);
        await WaitAllStartedAsync(factory, id);
        factory.Agents[2].Fail(StatusCode.DeadlineExceeded);

        var aborted = await Api.WaitForStatusAsync(client, id, "aborted");
        Assert.Equal($"Агент 3 из 3 ({CoordinatorFactory.AgentAddress(3)}): Истёк срок вызова агента", Error(aborted));
    }

    [Fact]
    public async Task Stop_stops_every_agent_and_keeps_only_common_steps()
    {
        await using var factory = new CoordinatorFactory { AgentCount = 3 };
        var client = factory.CreateClient();

        var id = await CreateRunAsync(client);
        await WaitAllStartedAsync(factory, id);
        SendStepOnAll(factory, 1);
        await Api.WaitForAsync(client, id, run => Api.StepCount(run) == 1);
        factory.Agents[0].SendStep(Part(2, 1000, 20));

        using var response = await Api.StopAsync(client, id);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stopped = await Api.ReadJsonAsync(response);
        Assert.Equal("aborted", Api.Status(stopped));
        Assert.Equal(JsonValueKind.Null, stopped.GetProperty("error").ValueKind);
        Assert.Equal(1, Api.StepCount(stopped));
        Assert.All(factory.Agents, agent => Assert.Equal(new[] { id }, agent.StopCalls));
    }

    [Fact]
    public async Task Busy_agent_makes_the_whole_group_retry()
    {
        await using var factory = new CoordinatorFactory { AgentCount = 3 };
        // Третий агент занят на первой попытке; остальным первая попытка отменяется
        factory.Agents[2].Fail(StatusCode.ResourceExhausted, "agent is busy");
        var client = factory.CreateClient();

        var id = await CreateRunAsync(client);
        // Обе попытки дошли до всех агентов: события ниже прочитает вторая
        await WaitAllStartedAsync(factory, id);
        await WaitAllStartedAsync(factory, id);

        SendStepOnAll(factory, 1);
        SendStepOnAll(factory, 2);
        foreach (var agent in factory.Agents)
            agent.Finish();

        var done = await Api.WaitForStatusAsync(client, id, "completed");
        Assert.Equal(2, Api.StepCount(done));
        Assert.All(factory.Agents, agent => Assert.Equal(new[] { id, id }, agent.RunCalls));
    }

    [Fact]
    public async Task Busy_agent_on_every_attempt_fails_the_run()
    {
        await using var factory = new CoordinatorFactory { AgentCount = 3, BusyRetryCount = 1 };
        factory.Agents[1].Fail(StatusCode.ResourceExhausted, "agent is busy");
        factory.Agents[1].Fail(StatusCode.ResourceExhausted, "agent is busy");
        var client = factory.CreateClient();

        var id = await CreateRunAsync(client);

        var failed = await Api.WaitForStatusAsync(client, id, "failed");
        Assert.Equal($"Агент 2 из 3 ({CoordinatorFactory.AgentAddress(2)}): Агент занят", Error(failed));
        Assert.Equal(0, Api.StepCount(failed));
        Assert.All(factory.Agents, agent => Assert.Equal(new[] { id, id }, agent.RunCalls));
    }

    [Theory]
    [InlineData("http://agent-1:50051", "http://AGENT-1:50051/", "не должны повторяться")]
    [InlineData("http://agent-1:50051", "not-a-url", "список абсолютных URL")]
    public async Task Coordinator_does_not_start_with_bad_agent_addresses(string first, string second, string message)
    {
        await using var factory = new CoordinatorFactory
        {
            Settings =
            {
                ["Agent:Addresses:0"] = first,
                ["Agent:Addresses:1"] = second,
            },
        };

        var error = Record.Exception(() => { factory.CreateClient(); });

        Assert.NotNull(error);
        Assert.Contains(message, error.ToString());
    }
}

// agent_count в SQLite (#78)
public sealed class AgentCountStoreTests : IDisposable
{
    private readonly string _path = TestData.NewDatabasePath();

    public void Dispose() => TestData.DeleteDatabase(_path);

    private RunStore Open() => new(Options.Create(new StorageOptions { Path = _path }));

    private static Scenario Scenario() => new()
    {
        Name = "ramp",
        Target = new Target { Url = "http://abstock:8080/", Method = "GET" },
        Steps = [new Step { TargetRps = 150, DurationSeconds = 10 }],
    };

    [Fact]
    public void Agent_count_is_read_back_after_store_is_recreated()
    {
        var id = Open().Create(Scenario(), agentCount: 3).Id;

        Assert.Equal(3, Open().Get(id)!.AgentCount);
    }

    [Fact]
    public void Database_without_agent_count_is_upgraded()
    {
        // Схема до #78
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var id = Guid.NewGuid();
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE runs (
                    seq INTEGER PRIMARY KEY AUTOINCREMENT,
                    id TEXT NOT NULL UNIQUE,
                    status TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    started_at TEXT,
                    finished_at TEXT,
                    error TEXT,
                    scenario_name TEXT,
                    scenario TEXT NOT NULL,
                    analysis TEXT
                );
                CREATE TABLE steps (
                    run_id TEXT NOT NULL REFERENCES runs (id),
                    position INTEGER NOT NULL,
                    step TEXT NOT NULL,
                    PRIMARY KEY (run_id, position)
                );
                INSERT INTO runs (id, status, created_at, finished_at, scenario_name, scenario)
                VALUES ($id, 'completed', '2026-10-01T10:00:00.0000000+00:00', '2026-10-01T10:05:00.0000000+00:00', 'old',
                        '{"name":"old","target":{"url":"http://abstock:8080/","method":"GET"},"steps":[{"target_rps":100,"duration_seconds":45}]}');
                """;
            command.Parameters.AddWithValue("$id", id.ToString());
            command.ExecuteNonQuery();
        }

        var store = Open();

        var old = store.Get(id)!;
        Assert.Equal(1, old.AgentCount);
        Assert.Equal(RunStatus.Completed, old.Status);
        Assert.Equal(3, store.Get(store.Create(Scenario(), agentCount: 3).Id)!.AgentCount);
        // Повторное открытие не добавляет столбец второй раз
        Assert.Equal(1, Open().Get(id)!.AgentCount);
    }
}
