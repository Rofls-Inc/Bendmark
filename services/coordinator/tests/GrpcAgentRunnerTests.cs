using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using Proto = Bendmark.Contracts.V1;

namespace Bendmark.Coordinator.Tests;

// GrpcAgentRunner против настоящего gRPC-сервера в памяти процесса
public class GrpcAgentRunnerTests
{
    private static readonly Scenario TwoSteps = new()
    {
        Name = "two-steps",
        Target = new Target { Url = "http://abstock:8080/", Method = "GET" },
        Steps =
        [
            new Step { TargetRps = 100, DurationSeconds = 45 },
            new Step { TargetRps = 150, DurationSeconds = 45.5 },
        ],
    };

    [Fact]
    public async Task Sends_whole_scenario_and_maps_streamed_steps()
    {
        var agent = new ScriptedAgent
        {
            Script = async (request, stream, _) =>
            {
                await stream.WriteAsync(Response(request, ProtoStep(1, 100, 45, requests: 4500, errors: 0)));
                await stream.WriteAsync(Response(request, ProtoStep(2, 150, 45.5, requests: 6825, errors: 27, skipped: 3)));
            },
        };
        await using var host = await AgentHost.StartAsync(agent);
        var runner = host.CreateRunner();
        var id = Guid.NewGuid();

        var steps = new List<StepResultDto>();
        await foreach (var step in runner.RunAsync(id, TwoSteps, CancellationToken.None))
            steps.Add(step);

        var received = agent.Received!;
        Assert.Equal(id.ToString(), received.RunId);
        Assert.Equal("two-steps", received.Scenario.Name);
        Assert.Equal("http://abstock:8080/", received.Scenario.Target.Url);
        Assert.Equal("GET", received.Scenario.Target.Method);
        Assert.Equal(2, received.Scenario.Steps.Count);
        Assert.Equal(150.0, received.Scenario.Steps[1].TargetRps);
        Assert.Equal(45.5, received.Scenario.Steps[1].DurationSeconds);

        // Срок вызова: 45 + 45.5 + 2 × 5 + 30 = 130.5 с
        var remaining = (agent.ReceivedDeadline - DateTime.UtcNow).TotalSeconds;
        Assert.InRange(remaining, 100.0, 131.0);

        Assert.Equal(2, steps.Count);
        var second = steps[1];
        Assert.Equal(2u, second.Index);
        Assert.Equal(150.0, second.TargetRps);
        Assert.Equal(45.5, second.DurationSeconds);
        Assert.Equal(6825UL, second.RequestCount);
        Assert.Equal(150.0, second.ThroughputRps);
        Assert.Equal(new LatencyDto { P50 = 13, P90 = 20, P99 = 26 }, second.LatencyMs);
        Assert.Equal(27UL, second.Errors.Count);
        Assert.Equal(0.4, second.Errors.RatePercent);
        Assert.Equal(3UL, second.SkippedCount);
    }

    [Fact]
    public async Task Stop_ends_run_stream_with_cancelled()
    {
        var agent = new ScriptedAgent();
        agent.Script = async (request, stream, context) =>
        {
            await stream.WriteAsync(Response(request, ProtoStep(1, 100, 45, requests: 4500, errors: 0)));
            // Как настоящий агент: держит поток до Stop, затем завершает его с CANCELLED
            await agent.StopSignal.Task.WaitAsync(context.CancellationToken);
            throw new RpcException(new Status(StatusCode.Cancelled, "остановлен"));
        };
        await using var host = await AgentHost.StartAsync(agent);
        var runner = host.CreateRunner();
        var id = Guid.NewGuid();

        await using var steps = runner.RunAsync(id, TwoSteps, CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await steps.MoveNextAsync());
        Assert.Equal(1u, steps.Current.Index);

        Assert.True(await runner.StopAsync(id, CancellationToken.None));
        Assert.Equal(id.ToString(), agent.StoppedRunId);

        var error = await Assert.ThrowsAsync<RpcException>(async () => await steps.MoveNextAsync());
        Assert.Equal(StatusCode.Cancelled, error.StatusCode);
    }

    [Fact]
    public async Task Agent_status_is_passed_through_as_rpc_exception()
    {
        var agent = new ScriptedAgent
        {
            Script = (_, _, _) => throw new RpcException(new Status(StatusCode.ResourceExhausted, "занят")),
        };
        await using var host = await AgentHost.StartAsync(agent);
        var runner = host.CreateRunner();

        var error = await Assert.ThrowsAsync<RpcException>(async () =>
        {
            await foreach (var _ in runner.RunAsync(Guid.NewGuid(), TwoSteps, CancellationToken.None))
            {
            }
        });
        Assert.Equal(StatusCode.ResourceExhausted, error.StatusCode);
    }

    private static Proto.RunResponse Response(Proto.RunRequest request, Proto.StepResult step) =>
        new() { RunId = request.RunId, StepResult = step };

    private static Proto.StepResult ProtoStep(
        uint index, double targetRps, double duration, ulong requests, ulong errors, ulong skipped = 0) => new()
    {
        Index = index,
        TargetRps = targetRps,
        DurationSeconds = duration,
        RequestCount = requests,
        ThroughputRps = requests / duration,
        LatencyMs = new Proto.LatencyPercentiles { P50 = 13, P90 = 20, P99 = 26 },
        // Агентская доля ошибок намеренно неверна: координатор считает её сам из счётчиков
        Errors = new Proto.RequestErrors { Count = errors, RatePercent = 99 },
        SkippedCount = skipped,
    };
}

public sealed class ScriptedAgent : Proto.AgentService.AgentServiceBase
{
    public Func<Proto.RunRequest, IServerStreamWriter<Proto.RunResponse>, ServerCallContext, Task>? Script { get; set; }

    public TaskCompletionSource StopSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Proto.RunRequest? Received { get; private set; }

    public DateTime ReceivedDeadline { get; private set; }

    public string? StoppedRunId { get; private set; }

    public override async Task Run(
        Proto.RunRequest request, IServerStreamWriter<Proto.RunResponse> responseStream, ServerCallContext context)
    {
        Received = request;
        ReceivedDeadline = context.Deadline;
        await Script!(request, responseStream, context);
    }

    public override Task<Proto.StopResponse> Stop(Proto.StopRequest request, ServerCallContext context)
    {
        StoppedRunId = request.RunId;
        return Task.FromResult(new Proto.StopResponse { WasRunning = StopSignal.TrySetResult() });
    }
}

internal sealed class AgentHost(WebApplication app, GrpcChannel channel) : IAsyncDisposable
{
    public static async Task<AgentHost> StartAsync(ScriptedAgent agent)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(agent);

        var app = builder.Build();
        app.MapGrpcService<ScriptedAgent>();
        await app.StartAsync();

        var server = app.GetTestServer();
        var channel = GrpcChannel.ForAddress(server.BaseAddress,
            new GrpcChannelOptions { HttpHandler = server.CreateHandler() });
        return new AgentHost(app, channel);
    }

    public GrpcAgentRunner CreateRunner() => new(channel, Options.Create(new AgentOptions()));

    public async ValueTask DisposeAsync()
    {
        channel.Dispose();
        await app.DisposeAsync();
    }
}
