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

// GrpcAnalyzer против настоящего gRPC-сервера в памяти процесса
public class GrpcAnalyzerTests
{
    [Fact]
    public async Task Sends_steps_without_options_and_maps_found_limit()
    {
        var analyzer = new ScriptedAnalyzer
        {
            Respond = _ => new Proto.FindLimitResponse
            {
                Limit = new Proto.Limit
                {
                    Found = true,
                    LimitRps = 350,
                    FailedStepIndex = 7,
                    Reasons = { "p99 812 мс выше порога 500 мс", "ошибок 2.4 % при пороге 1 %" },
                },
            },
        };
        await using var host = await AnalyzerHost.StartAsync(analyzer);

        var limit = await host.CreateClient().FindLimitAsync(
            [TestData.Step(1, 100), TestData.Step(2, 150, 6825, errorCount: 27, skippedCount: 3)],
            CancellationToken.None);

        Assert.True(limit.Found);
        Assert.Equal(350.0, limit.LimitRps);
        Assert.Equal(7u, limit.FailedStepIndex);
        Assert.Equal(new[] { "p99 812 мс выше порога 500 мс", "ошибок 2.4 % при пороге 1 %" }, limit.Reasons);

        var request = analyzer.Received!;
        Assert.Null(request.Options);
        Assert.Equal(2, request.Steps.Count);
        var second = request.Steps[1];
        Assert.Equal(2u, second.Index);
        Assert.Equal(150.0, second.TargetRps);
        Assert.Equal(45.0, second.DurationSeconds);
        Assert.Equal(6825UL, second.RequestCount);
        Assert.Equal(6825 / 45.0, second.ThroughputRps);
        Assert.Equal(22.0, second.LatencyMs.P99);
        Assert.Equal(27UL, second.Errors.Count);
        Assert.Equal(0.4, second.Errors.RatePercent);
        Assert.Equal(3UL, second.SkippedCount);
        Assert.True(analyzer.ReceivedDeadline < DateTime.UtcNow.AddSeconds(11));
    }

    [Fact]
    public async Task Absent_optional_fields_become_null()
    {
        var analyzer = new ScriptedAnalyzer
        {
            Respond = _ => new Proto.FindLimitResponse { Limit = new Proto.Limit { Found = false } },
        };
        await using var host = await AnalyzerHost.StartAsync(analyzer);

        var limit = await host.CreateClient().FindLimitAsync([TestData.Step(1, 100)], CancellationToken.None);

        Assert.False(limit.Found);
        Assert.Null(limit.LimitRps);
        Assert.Null(limit.FailedStepIndex);
        Assert.Empty(limit.Reasons);
    }

    [Fact]
    public async Task Analyzer_status_is_passed_through_as_rpc_exception()
    {
        var analyzer = new ScriptedAnalyzer
        {
            Respond = _ => throw new RpcException(new Status(StatusCode.FailedPrecondition, "ступень 1: пропуски")),
        };
        await using var host = await AnalyzerHost.StartAsync(analyzer);

        var error = await Assert.ThrowsAsync<RpcException>(() =>
            host.CreateClient().FindLimitAsync([TestData.Step(1, 100)], CancellationToken.None));
        Assert.Equal(StatusCode.FailedPrecondition, error.StatusCode);
        Assert.Equal("ступень 1: пропуски", error.Status.Detail);
    }
}

public sealed class ScriptedAnalyzer : Proto.AnalyzerService.AnalyzerServiceBase
{
    public Func<Proto.FindLimitRequest, Proto.FindLimitResponse>? Respond { get; init; }

    public Proto.FindLimitRequest? Received { get; private set; }

    public DateTime ReceivedDeadline { get; private set; }

    public override Task<Proto.FindLimitResponse> FindLimit(Proto.FindLimitRequest request, ServerCallContext context)
    {
        Received = request;
        ReceivedDeadline = context.Deadline;
        return Task.FromResult(Respond!(request));
    }
}

internal sealed class AnalyzerHost(WebApplication app, GrpcChannel channel) : IAsyncDisposable
{
    public static async Task<AnalyzerHost> StartAsync(ScriptedAnalyzer analyzer)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(analyzer);

        var app = builder.Build();
        app.MapGrpcService<ScriptedAnalyzer>();
        await app.StartAsync();

        var server = app.GetTestServer();
        var channel = GrpcChannel.ForAddress(server.BaseAddress,
            new GrpcChannelOptions { HttpHandler = server.CreateHandler() });
        return new AnalyzerHost(app, channel);
    }

    public GrpcAnalyzer CreateClient() => new(channel, Options.Create(new AnalyzerOptions()));

    public async ValueTask DisposeAsync()
    {
        channel.Dispose();
        await app.DisposeAsync();
    }
}
