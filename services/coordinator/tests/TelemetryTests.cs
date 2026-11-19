using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Bendmark.Coordinator.Tests;

public sealed class TelemetryTests
{
    [Fact]
    public async Task Report_keeps_ordered_snapshots_after_restart()
    {
        var idle = TestData.Read("telemetry", "idle.json");
        var missing = TestData.Read("telemetry", "missing-fields.json");
        var polls = 0;
        await using var source = await StartSourceAsync(context =>
            WriteSnapshotAsync(context, Interlocked.Increment(ref polls) == 1 ? idle : missing));

        var database = TestData.NewDatabasePath();
        try
        {
            Guid id;
            JsonElement saved;
            await using (var factory = WithTelemetry(source, database))
            {
                using var client = factory.CreateClient();
                id = await Api.CreateRunAsync(client);
                Assert.Equal(id, await factory.Agent.WaitStartedAsync());
                await Api.WaitForAsync(client, id, run => Snapshots(run).GetArrayLength() >= 2);
                Complete(factory.Agent);
                saved = await Api.WaitForStatusAsync(client, id, "completed");

                Assert.Equal("complete", TelemetryStatusOf(saved));
                Assert.Equal(0, Telemetry(saved).GetProperty("failed_polls").GetInt32());
                var samples = Snapshots(saved);
                Assert.All(samples.EnumerateArray(), sample =>
                {
                    Assert.Equal(id, sample.GetProperty("run_id").GetGuid());
                    Assert.EndsWith("Z", sample.GetProperty("observed_at").GetString());
                });
                Assert.Equal("2026-11-17T10:00:01.002Z", samples[0].GetProperty("snapshot").GetProperty("captured_at").GetString());
                Assert.Equal(0, samples[0].GetProperty("snapshot").GetProperty("queue_length").GetInt64());
                Assert.Equal(JsonValueKind.Null, samples[1].GetProperty("snapshot").GetProperty("queue_length").ValueKind);
            }

            await using var restarted = CoordinatorFactory.Reusing(database);
            using var restartedClient = restarted.CreateClient();
            var loaded = await Api.GetRunAsync(restartedClient, id);
            Assert.True(JsonElement.DeepEquals(Telemetry(saved), Telemetry(loaded)));

            var next = await Api.CreateRunAsync(restartedClient);
            Assert.Equal(next, await restarted.Agent.WaitStartedAsync());
            Complete(restarted.Agent);
            var other = await Api.WaitForStatusAsync(restartedClient, next, "completed");
            Assert.Equal("disabled", TelemetryStatusOf(other));
            Assert.Empty(Snapshots(other).EnumerateArray());
        }
        finally
        {
            TestData.DeleteDatabase(database);
        }
    }

    [Theory]
    [InlineData(false, "unavailable")]
    [InlineData(true, "partial")]
    public async Task Bad_responses_do_not_prevent_load_test_completion(bool recovers, string expectedStatus)
    {
        var idle = TestData.Read("telemetry", "idle.json");
        var polls = 0;
        await using var source = await StartSourceAsync(async context =>
        {
            switch (Interlocked.Increment(ref polls))
            {
                case 1:
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    break;
                case 2:
                    await WriteSnapshotAsync(context, "{broken");
                    break;
                default:
                    if (recovers)
                        await WriteSnapshotAsync(context, idle);
                    else
                        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    break;
            }
        });

        await using var factory = WithTelemetry(source);
        using var client = factory.CreateClient();
        var id = await Api.CreateRunAsync(client);
        Assert.Equal(id, await factory.Agent.WaitStartedAsync());

        await Api.WaitForAsync(client, id, run =>
            Telemetry(run).GetProperty("failed_polls").GetInt32() >= 2 &&
            (!recovers || Snapshots(run).GetArrayLength() > 0));
        Complete(factory.Agent);

        var done = await Api.WaitForStatusAsync(client, id, "completed");
        Assert.Equal(7, Api.StepCount(done));
        Assert.Equal(expectedStatus, TelemetryStatusOf(done));
        Assert.Equal(recovers, Snapshots(done).GetArrayLength() > 0);
    }

    [Fact]
    public async Task Timeout_covers_body_and_stop_cancels_pending_request()
    {
        var secondPoll = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var polls = 0;
        await using var source = await StartSourceAsync(async context =>
        {
            await context.Response.StartAsync();
            if (Interlocked.Increment(ref polls) == 2)
                secondPoll.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
        });

        await using var factory = WithTelemetry(source);
        factory.Settings["Telemetry:TimeoutSeconds"] = "0.25";
        using var client = factory.CreateClient();
        var id = await Api.CreateRunAsync(client);
        Assert.Equal(id, await factory.Agent.WaitStartedAsync());
        await secondPoll.Task.WaitAsync(FakeAgentRunner.Timeout);
        using var stopped = await Api.StopAsync(client, id);
        stopped.EnsureSuccessStatusCode();

        var done = await Api.WaitForStatusAsync(client, id, "aborted");
        Assert.Equal("unavailable", TelemetryStatusOf(done));
        Assert.True(Telemetry(done).GetProperty("failed_polls").GetInt32() >= 1);
        Assert.Empty(Snapshots(done).EnumerateArray());
    }

    [Fact]
    public async Task Completing_load_cancels_poll_without_waiting_for_http_timeout()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var source = await StartSourceAsync(async context =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
        });

        await using var factory = WithTelemetry(source);
        factory.Settings["Telemetry:TimeoutSeconds"] = "30";
        using var client = factory.CreateClient();
        var id = await Api.CreateRunAsync(client);
        Assert.Equal(id, await factory.Agent.WaitStartedAsync());
        await started.Task.WaitAsync(FakeAgentRunner.Timeout);
        Complete(factory.Agent);

        var done = await Api.WaitForStatusAsync(client, id, "completed");
        Assert.Equal("unavailable", TelemetryStatusOf(done));
        Assert.Equal(0, Telemetry(done).GetProperty("failed_polls").GetInt32());
    }

    private static async Task<WebApplication> StartSourceAsync(RequestDelegate respond)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.MapGet("/telemetry", respond);
        await app.StartAsync();
        return app;
    }

    private static CoordinatorFactory WithTelemetry(WebApplication source, string? database = null)
    {
        var factory = database is null ? new CoordinatorFactory() : CoordinatorFactory.Reusing(database);
        factory.Settings["Telemetry:Url"] = source.Urls.Single() + "/telemetry";
        factory.Settings["Telemetry:IntervalSeconds"] = "0.05";
        factory.Settings["Telemetry:TimeoutSeconds"] = "2";
        return factory;
    }

    private static Task WriteSnapshotAsync(HttpContext context, string json)
    {
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(json);
    }

    private static void Complete(FakeAgentRunner agent)
    {
        for (uint i = 1; i <= 7; i++)
            agent.SendStep(TestData.Step(i));
        agent.Finish();
    }

    private static JsonElement Telemetry(JsonElement run) => run.GetProperty("telemetry");
    private static JsonElement Snapshots(JsonElement run) => Telemetry(run).GetProperty("snapshots");
    private static string? TelemetryStatusOf(JsonElement run) => Telemetry(run).GetProperty("status").GetString();
}
