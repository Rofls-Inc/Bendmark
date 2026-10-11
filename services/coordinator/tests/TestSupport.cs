using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Grpc.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

// Каждый тест поднимает свой хост; параллельное построение хостов мешает WebApplicationFactory
// перехватывать именно хост координатора, поэтому тесты идут последовательно
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Bendmark.Coordinator.Tests;

// Координатор с фейковыми агентом и анализатором вместо gRPC
public sealed class CoordinatorFactory : WebApplicationFactory<Program>
{
    public FakeAgentRunner Agent { get; } = new();

    public FakeAnalyzer Analyzer { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IAgentRunner>(Agent);
            services.AddSingleton<IAnalyzer>(Analyzer);
        });
}

// Анализатор, ответ которого задаёт тест. По умолчанию: отказа нет, нижняя оценка — последняя ступень
public sealed class FakeAnalyzer : IAnalyzer
{
    private readonly ConcurrentQueue<IReadOnlyList<StepResultDto>> _calls = new();

    public IReadOnlyCollection<IReadOnlyList<StepResultDto>> Calls => _calls;

    public Func<IReadOnlyList<StepResultDto>, Task<LimitResult>> Respond { get; set; } =
        steps => Task.FromResult(new LimitResult(false, steps[^1].TargetRps, null, []));

    public Task<LimitResult> FindLimitAsync(IReadOnlyList<StepResultDto> steps, CancellationToken cancellationToken)
    {
        _calls.Enqueue(steps);
        return Respond(steps);
    }
}

// Агент, которым управляет тест: ступени, завершение и ошибки отправляются вручную.
// Stop ведёт себя как настоящий агент: поток Run завершается с CANCELLED
public sealed class FakeAgentRunner : IAgentRunner
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static readonly object Done = new();
    private readonly Channel<object> _events = Channel.CreateUnbounded<object>();
    private readonly Channel<Guid> _started = Channel.CreateUnbounded<Guid>();
    private readonly ConcurrentQueue<Guid> _stopCalls = new();

    public IReadOnlyCollection<Guid> StopCalls => _stopCalls;

    public void SendStep(StepResultDto step) => _events.Writer.TryWrite(step);

    public void Finish() => _events.Writer.TryWrite(Done);

    public void Fail(StatusCode code, string detail = "fake") =>
        _events.Writer.TryWrite(new RpcException(new Status(code, detail)));

    // id следующего прогона, который дошёл до агента
    public async Task<Guid> WaitStartedAsync() =>
        await _started.Reader.ReadAsync().AsTask().WaitAsync(Timeout);

    public async IAsyncEnumerable<StepResultDto> RunAsync(
        Guid runId,
        Scenario scenario,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _started.Writer.TryWrite(runId);
        while (true)
        {
            var next = await _events.Reader.ReadAsync(cancellationToken);
            if (next is StepResultDto step)
                yield return step;
            else if (next is Exception error)
                throw error;
            else
                yield break;
        }
    }

    public Task<bool> StopAsync(Guid runId, CancellationToken cancellationToken)
    {
        _stopCalls.Enqueue(runId);
        Fail(StatusCode.Cancelled, "остановлен");
        return Task.FromResult(true);
    }
}

public static class TestData
{
    public static string Read(string kind, string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "testdata", kind, name));

    public static StepResultDto Step(uint index, double targetRps = 100, ulong requestCount = 4500,
        ulong errorCount = 0, ulong skippedCount = 0) =>
        StepResultDto.Create(
            index,
            targetRps,
            durationSeconds: 45,
            requestCount,
            throughputRps: requestCount / 45.0,
            new LatencyDto { P50 = 12, P90 = 18, P99 = 22 },
            errorCount,
            skippedCount);

    // Ступень из файла result.json; rate_percent пересчитывается из счётчиков
    public static StepResultDto StepFromJson(JsonElement step)
    {
        var latency = step.GetProperty("latency_ms");
        return StepResultDto.Create(
            step.GetProperty("index").GetUInt32(),
            step.GetProperty("target_rps").GetDouble(),
            step.GetProperty("duration_seconds").GetDouble(),
            step.GetProperty("request_count").GetUInt64(),
            step.GetProperty("throughput_rps").GetDouble(),
            new LatencyDto
            {
                P50 = latency.GetProperty("p50").GetDouble(),
                P90 = latency.GetProperty("p90").GetDouble(),
                P99 = latency.GetProperty("p99").GetDouble(),
            },
            step.GetProperty("errors").GetProperty("count").GetUInt64(),
            step.TryGetProperty("skipped_count", out var skipped) ? skipped.GetUInt64() : 0);
    }
}

public static class Api
{
    public static async Task<HttpResponseMessage> PostScenarioAsync(
        HttpClient client, string name, string contentType) =>
        await client.PostAsync("/runs",
            new StringContent(TestData.Read("scenarios", name), Encoding.UTF8, contentType));

    public static async Task<Guid> CreateRunAsync(HttpClient client, string name = "ramp.json")
    {
        using var response = await PostScenarioAsync(client, name, "application/json");
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        return (await ReadJsonAsync(response)).GetProperty("id").GetGuid();
    }

    public static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    public static async Task<JsonElement> GetRunAsync(HttpClient client, Guid id)
    {
        using var response = await client.GetAsync($"/runs/{id}");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        return await ReadJsonAsync(response);
    }

    public static async Task<HttpResponseMessage> StopAsync(HttpClient client, Guid id) =>
        await client.PostAsync($"/runs/{id}/stop", null);

    // Опрашивает GET /runs/{id}, пока прогон не придёт в нужное состояние
    public static async Task<JsonElement> WaitForAsync(HttpClient client, Guid id, Func<JsonElement, bool> condition)
    {
        var deadline = DateTime.UtcNow + FakeAgentRunner.Timeout;
        while (true)
        {
            var run = await GetRunAsync(client, id);
            if (condition(run))
                return run;
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"Прогон не пришёл в ожидаемое состояние: {run}");
            await Task.Delay(20);
        }
    }

    public static Task<JsonElement> WaitForStatusAsync(HttpClient client, Guid id, string status) =>
        WaitForAsync(client, id, run => Status(run) == status);

    public static string Status(JsonElement run) => run.GetProperty("status").GetString()!;

    public static int StepCount(JsonElement run) => run.GetProperty("steps").GetArrayLength();
}
