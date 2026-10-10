using System.Net;
using System.Text.Json;
using Grpc.Core;
using Xunit;

namespace Bendmark.Coordinator.Tests;

public class RunLifecycleTests
{
    [Fact]
    public async Task Run_goes_running_then_completed_with_steps()
    {
        await using var factory = new CoordinatorFactory();
        var client = factory.CreateClient();

        var id = await Api.CreateRunAsync(client);
        Assert.Equal(id, await factory.Agent.WaitStartedAsync());

        var running = await Api.GetRunAsync(client, id);
        Assert.Equal("running", Api.Status(running));
        Assert.Equal(JsonValueKind.String, running.GetProperty("started_at").ValueKind);
        Assert.Equal(JsonValueKind.Null, running.GetProperty("finished_at").ValueKind);
        Assert.Equal(0, Api.StepCount(running));

        factory.Agent.SendStep(TestData.Step(1, 100, 4500));
        factory.Agent.SendStep(TestData.Step(2, 150, 6750, errorCount: 27));
        var withSteps = await Api.WaitForAsync(client, id, run => Api.StepCount(run) == 2);
        Assert.Equal("running", Api.Status(withSteps));

        SendRampSteps(factory.Agent, skip: 2);
        factory.Agent.Finish();
        var done = await Api.WaitForStatusAsync(client, id, "completed");
        Assert.Equal(JsonValueKind.String, done.GetProperty("finished_at").ValueKind);
        Assert.Equal(JsonValueKind.Null, done.GetProperty("error").ValueKind);
        Assert.Equal("abstock-home-ramp", done.GetProperty("scenario").GetProperty("name").GetString());

        var steps = done.GetProperty("steps");
        Assert.Equal(7, steps.GetArrayLength());
        Assert.Equal(1, steps[0].GetProperty("index").GetInt32());
        Assert.Equal(2, steps[1].GetProperty("index").GetInt32());
        Assert.Equal(6750UL, steps[1].GetProperty("request_count").GetUInt64());
        Assert.Equal(27UL, steps[1].GetProperty("errors").GetProperty("count").GetUInt64());
        Assert.Equal(0.4, steps[1].GetProperty("errors").GetProperty("rate_percent").GetDouble());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(6)]
    public async Task Successful_stream_with_missing_steps_fails_run(int receivedStepCount)
    {
        await using var factory = new CoordinatorFactory();
        var client = factory.CreateClient();
        var id = await Api.CreateRunAsync(client);
        await factory.Agent.WaitStartedAsync();

        SendRampSteps(factory.Agent, take: receivedStepCount);
        factory.Agent.Finish();

        var run = await Api.WaitForAsync(client, id,
            run => Api.Status(run) is "completed" or "failed" or "aborted");
        Assert.Equal("failed", Api.Status(run));
        Assert.Equal(receivedStepCount, Api.StepCount(run));
        Assert.Contains($"получено {receivedStepCount} из 7", run.GetProperty("error").GetString());
        Assert.Equal(JsonValueKind.String, run.GetProperty("finished_at").ValueKind);

        using var result = await client.GetAsync($"/runs/{id}/result");
        Assert.Equal(HttpStatusCode.Conflict, result.StatusCode);
    }

    [Fact]
    public async Task Extra_step_fails_run_without_saving_it()
    {
        await using var factory = new CoordinatorFactory();
        var client = factory.CreateClient();
        var id = await Api.CreateRunAsync(client);
        await factory.Agent.WaitStartedAsync();

        SendRampSteps(factory.Agent);
        factory.Agent.SendStep(TestData.Step(8, 450));
        factory.Agent.Finish();

        var run = await Api.WaitForAsync(client, id,
            run => Api.Status(run) is "completed" or "failed" or "aborted");
        Assert.Equal("failed", Api.Status(run));
        Assert.Equal(7, Api.StepCount(run));
        Assert.Contains("лишнюю ступень 8", run.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData(StatusCode.InvalidArgument)]
    [InlineData(StatusCode.Internal)]
    [InlineData(StatusCode.Unavailable)]
    public async Task Agent_error_fails_run_and_keeps_received_steps(StatusCode code)
    {
        await using var factory = new CoordinatorFactory();
        var client = factory.CreateClient();
        var id = await Api.CreateRunAsync(client);
        await factory.Agent.WaitStartedAsync();

        factory.Agent.SendStep(TestData.Step(1));
        factory.Agent.Fail(code, "сломался");

        var run = await Api.WaitForStatusAsync(client, id, "failed");
        Assert.Contains("сломался", run.GetProperty("error").GetString());
        Assert.Equal(1, Api.StepCount(run));
        Assert.Equal(JsonValueKind.String, run.GetProperty("finished_at").ValueKind);
    }

    [Fact]
    public async Task Busy_agent_fails_run()
    {
        await using var factory = new CoordinatorFactory();
        var client = factory.CreateClient();
        var id = await Api.CreateRunAsync(client);
        await factory.Agent.WaitStartedAsync();

        factory.Agent.Fail(StatusCode.ResourceExhausted);

        var run = await Api.WaitForStatusAsync(client, id, "failed");
        Assert.Equal("Агент занят", run.GetProperty("error").GetString());
        Assert.Equal(0, Api.StepCount(run));
    }

    [Fact]
    public async Task Deadline_exceeded_aborts_run_with_completed_steps()
    {
        await using var factory = new CoordinatorFactory();
        var client = factory.CreateClient();
        var id = await Api.CreateRunAsync(client);
        await factory.Agent.WaitStartedAsync();

        factory.Agent.SendStep(TestData.Step(1));
        factory.Agent.Fail(StatusCode.DeadlineExceeded);

        var run = await Api.WaitForStatusAsync(client, id, "aborted");
        Assert.Equal(1, Api.StepCount(run));
        Assert.False(string.IsNullOrEmpty(run.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task Steps_out_of_order_fail_run()
    {
        await using var factory = new CoordinatorFactory();
        var client = factory.CreateClient();
        var id = await Api.CreateRunAsync(client);
        await factory.Agent.WaitStartedAsync();

        factory.Agent.SendStep(TestData.Step(2));

        var run = await Api.WaitForStatusAsync(client, id, "failed");
        Assert.Equal(0, Api.StepCount(run));
    }

    [Fact]
    public async Task Stop_during_run_aborts_and_keeps_only_completed_steps()
    {
        await using var factory = new CoordinatorFactory();
        var client = factory.CreateClient();
        var id = await Api.CreateRunAsync(client);
        await factory.Agent.WaitStartedAsync();

        factory.Agent.SendStep(TestData.Step(1));
        await Api.WaitForAsync(client, id, run => Api.StepCount(run) == 1);

        using var response = await Api.StopAsync(client, id);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stopped = await Api.ReadJsonAsync(response);
        Assert.Equal("aborted", Api.Status(stopped));
        Assert.Equal(1, Api.StepCount(stopped));
        Assert.Equal(JsonValueKind.Null, stopped.GetProperty("error").ValueKind);
        Assert.Equal(new[] { id }, factory.Agent.StopCalls);

        var run = await Api.GetRunAsync(client, id);
        Assert.Equal("aborted", Api.Status(run));
        Assert.Equal(1, Api.StepCount(run));
    }

    [Fact]
    public async Task Stop_in_queue_aborts_without_calling_agent()
    {
        await using var factory = new CoordinatorFactory();
        var client = factory.CreateClient();

        var first = await Api.CreateRunAsync(client);
        Assert.Equal(first, await factory.Agent.WaitStartedAsync());
        var queued = await Api.CreateRunAsync(client);
        Assert.Equal("created", Api.Status(await Api.GetRunAsync(client, queued)));

        using (var response = await Api.StopAsync(client, queued))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("aborted", Api.Status(await Api.ReadJsonAsync(response)));
        }

        SendRampSteps(factory.Agent);
        factory.Agent.Finish();
        await Api.WaitForStatusAsync(client, first, "completed");

        // Следующим до агента доходит новый прогон: остановленный в очереди пропущен
        var next = await Api.CreateRunAsync(client);
        Assert.Equal(next, await factory.Agent.WaitStartedAsync());
        Assert.Empty(factory.Agent.StopCalls);

        var skipped = await Api.GetRunAsync(client, queued);
        Assert.Equal("aborted", Api.Status(skipped));
        Assert.Equal(JsonValueKind.Null, skipped.GetProperty("started_at").ValueKind);
        Assert.Equal(JsonValueKind.String, skipped.GetProperty("finished_at").ValueKind);
        Assert.Equal(0, Api.StepCount(skipped));
    }

    [Fact]
    public async Task Stop_of_finished_run_is_409_and_keeps_status()
    {
        await using var factory = new CoordinatorFactory();
        var client = factory.CreateClient();
        var id = await Api.CreateRunAsync(client);
        await factory.Agent.WaitStartedAsync();
        SendRampSteps(factory.Agent);
        factory.Agent.Finish();
        await Api.WaitForStatusAsync(client, id, "completed");

        using var response = await Api.StopAsync(client, id);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("completed", Api.Status(await Api.GetRunAsync(client, id)));
        Assert.Empty(factory.Agent.StopCalls);
    }

    [Fact]
    public async Task Stop_of_unknown_run_is_404()
    {
        await using var factory = new CoordinatorFactory();
        var client = factory.CreateClient();

        using var response = await Api.StopAsync(client, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Result_is_409_until_run_finishes()
    {
        await using var factory = new CoordinatorFactory();
        var client = factory.CreateClient();
        var id = await Api.CreateRunAsync(client);
        await factory.Agent.WaitStartedAsync();

        using var running = await client.GetAsync($"/runs/{id}/result");
        Assert.Equal(HttpStatusCode.Conflict, running.StatusCode);

        using var unknown = await client.GetAsync($"/runs/{Guid.NewGuid()}/result");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Result_of_failed_run_is_409()
    {
        await using var factory = new CoordinatorFactory();
        var client = factory.CreateClient();
        var id = await Api.CreateRunAsync(client);
        await factory.Agent.WaitStartedAsync();
        factory.Agent.Fail(StatusCode.Internal);
        await Api.WaitForStatusAsync(client, id, "failed");

        using var response = await client.GetAsync($"/runs/{id}/result");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Result_of_aborted_run_has_aborted_status_and_completed_steps()
    {
        await using var factory = new CoordinatorFactory();
        var client = factory.CreateClient();
        var id = await Api.CreateRunAsync(client);
        await factory.Agent.WaitStartedAsync();
        factory.Agent.SendStep(TestData.Step(1));
        factory.Agent.SendStep(TestData.Step(2, 150, 6750));
        await Api.WaitForAsync(client, id, run => Api.StepCount(run) == 2);
        using (await Api.StopAsync(client, id)) { }

        using var response = await client.GetAsync($"/runs/{id}/result");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await Api.ReadJsonAsync(response);
        Assert.Equal("abstock-home-ramp", result.GetProperty("scenario_name").GetString());
        Assert.Equal("aborted", result.GetProperty("status").GetString());
        Assert.Equal(2, result.GetProperty("steps").GetArrayLength());
    }

    private static void SendRampSteps(FakeAgentRunner agent, int skip = 0, int? take = null)
    {
        using var document = JsonDocument.Parse(TestData.Read("results", "limit-found.json"));
        var steps = document.RootElement.GetProperty("steps").EnumerateArray().Skip(skip);
        if (take is { } count)
            steps = steps.Take(count);
        foreach (var step in steps)
            agent.SendStep(TestData.StepFromJson(step));
    }
}
