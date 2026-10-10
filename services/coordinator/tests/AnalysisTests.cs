using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Grpc.Core;
using Xunit;

namespace Bendmark.Coordinator.Tests;

// Анализ завершённого прогона (#34) с фейковыми агентом и анализатором
public class AnalysisTests
{
    // Сценарий ровно из этих ступеней: completed ставится, только если агент прислал все
    private static async Task<(HttpClient Client, Guid Id)> StartRunAsync(CoordinatorFactory factory, params double[] rps)
    {
        var client = factory.CreateClient();
        var scenario = new
        {
            name = "analysis",
            target = new { url = "http://abstock:8080/", method = "GET" },
            steps = rps.Select(value => new { target_rps = value, duration_seconds = 45 }),
        };
        using var response = await client.PostAsJsonAsync("/runs", scenario);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await Api.ReadJsonAsync(response)).GetProperty("id").GetGuid();
        await factory.Agent.WaitStartedAsync();
        return (client, id);
    }

    private static Task<JsonElement> WaitForAnalysisAsync(HttpClient client, Guid id, string status) =>
        Api.WaitForAsync(client, id, run =>
            run.GetProperty("analysis").ValueKind == JsonValueKind.Object
            && run.GetProperty("analysis").GetProperty("status").GetString() == status);

    private static string Message(JsonElement analysis) => analysis.GetProperty("message").GetString()!;

    [Fact]
    public async Task Found_limit_is_shown_in_run()
    {
        await using var factory = new CoordinatorFactory();
        var (client, id) = await StartRunAsync(factory, 100, 150);
        factory.Analyzer.Respond = _ => Task.FromResult(
            new LimitResult(true, 100, 2, ["p99 900 мс выше порога 500 мс"]));

        factory.Agent.SendStep(TestData.Step(1, 100));
        factory.Agent.SendStep(TestData.Step(2, 150, 6750));
        factory.Agent.Finish();

        var run = await WaitForAnalysisAsync(client, id, "found");
        Assert.Equal("completed", Api.Status(run));
        var analysis = run.GetProperty("analysis");
        Assert.Equal(100.0, analysis.GetProperty("limit_rps").GetDouble());
        Assert.Equal(2, analysis.GetProperty("failed_step_index").GetInt32());
        Assert.Equal("p99 900 мс выше порога 500 мс", analysis.GetProperty("reasons")[0].GetString());
        Assert.Equal(2, analysis.GetProperty("analyzed_steps").GetInt32());
        Assert.Equal("Предел: 100 запр/с, отказ на ступени 2 (150 запр/с)", Message(analysis));

        var sent = Assert.Single(factory.Analyzer.Calls);
        Assert.Equal(new uint[] { 1, 2 }, sent.Select(step => step.Index));
    }

    [Fact]
    public async Task Failure_on_first_step_has_no_limit_rps()
    {
        await using var factory = new CoordinatorFactory();
        var (client, id) = await StartRunAsync(factory, 100);
        factory.Analyzer.Respond = _ => Task.FromResult(new LimitResult(true, null, 1, ["пропускная выросла на 30 запр/с при росте нагрузки на 100 запр/с"]));

        factory.Agent.SendStep(TestData.Step(1, 100));
        factory.Agent.Finish();

        var analysis = (await WaitForAnalysisAsync(client, id, "found")).GetProperty("analysis");
        Assert.Equal(JsonValueKind.Null, analysis.GetProperty("limit_rps").ValueKind);
        Assert.Equal("Предел ниже первой ступени: отказ уже на ступени 1 (100 запр/с)", Message(analysis));
    }

    [Fact]
    public async Task No_failure_gives_lower_bound()
    {
        await using var factory = new CoordinatorFactory();
        var (client, id) = await StartRunAsync(factory, 100, 200);

        factory.Agent.SendStep(TestData.Step(1, 100));
        factory.Agent.SendStep(TestData.Step(2, 200, 9000));
        factory.Agent.Finish();

        var analysis = (await WaitForAnalysisAsync(client, id, "not_found")).GetProperty("analysis");
        Assert.Equal(200.0, analysis.GetProperty("limit_rps").GetDouble());
        Assert.Equal(JsonValueKind.Null, analysis.GetProperty("failed_step_index").ValueKind);
        Assert.Equal(0, analysis.GetProperty("reasons").GetArrayLength());
        Assert.Equal("Отказа не было: предел не ниже 200 запр/с", Message(analysis));
    }

    [Fact]
    public async Task Failed_precondition_is_undetermined()
    {
        await using var factory = new CoordinatorFactory();
        var (client, id) = await StartRunAsync(factory, 100);
        factory.Analyzer.Respond = _ => throw new RpcException(
            new Status(StatusCode.FailedPrecondition, "ступень 1: нет завершившихся запросов"));

        factory.Agent.SendStep(TestData.Step(1, 100, requestCount: 0));
        factory.Agent.Finish();

        var run = await WaitForAnalysisAsync(client, id, "undetermined");
        Assert.Equal("completed", Api.Status(run));
        var analysis = run.GetProperty("analysis");
        Assert.Equal(JsonValueKind.Null, analysis.GetProperty("limit_rps").ValueKind);
        Assert.StartsWith("Предел не определён: агент не успевал подавать нагрузку или нет измерений", Message(analysis));
        Assert.Contains("нет завершившихся запросов", Message(analysis));
    }

    [Theory]
    [InlineData(StatusCode.Unavailable)]
    [InlineData(StatusCode.InvalidArgument)]
    [InlineData(StatusCode.Internal)]
    public async Task Analyzer_error_keeps_run_completed(StatusCode code)
    {
        await using var factory = new CoordinatorFactory();
        var (client, id) = await StartRunAsync(factory, 100);
        factory.Analyzer.Respond = _ => throw new RpcException(new Status(code, "сломался"));

        factory.Agent.SendStep(TestData.Step(1, 100));
        factory.Agent.Finish();

        var run = await WaitForAnalysisAsync(client, id, "error");
        Assert.Equal("completed", Api.Status(run));
        Assert.Equal(JsonValueKind.Null, run.GetProperty("error").ValueKind);
        Assert.Contains("сломался", Message(run.GetProperty("analysis")));
        Assert.Equal(1, Api.StepCount(run));
    }

    [Fact]
    public async Task Analysis_is_pending_until_analyzer_answers()
    {
        await using var factory = new CoordinatorFactory();
        var (client, id) = await StartRunAsync(factory, 100);
        var answer = new TaskCompletionSource<LimitResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Analyzer.Respond = _ => answer.Task;

        factory.Agent.SendStep(TestData.Step(1, 100));
        factory.Agent.Finish();

        var pending = await WaitForAnalysisAsync(client, id, "pending");
        Assert.Equal("completed", Api.Status(pending));

        answer.SetResult(new LimitResult(false, 100, null, []));
        await WaitForAnalysisAsync(client, id, "not_found");
    }

    [Fact]
    public async Task Failed_run_is_not_analyzed()
    {
        await using var factory = new CoordinatorFactory();
        var (client, id) = await StartRunAsync(factory, 100, 150);

        factory.Agent.SendStep(TestData.Step(1, 100));
        factory.Agent.Fail(StatusCode.Internal);

        var run = await Api.WaitForStatusAsync(client, id, "failed");
        Assert.Equal(JsonValueKind.Null, run.GetProperty("analysis").ValueKind);
        Assert.Empty(factory.Analyzer.Calls);
    }

    [Fact]
    public async Task Aborted_run_is_analyzed_by_completed_steps_with_note()
    {
        await using var factory = new CoordinatorFactory();
        var (client, id) = await StartRunAsync(factory, 100, 150);

        factory.Agent.SendStep(TestData.Step(1, 100));
        await Api.WaitForAsync(client, id, run => Api.StepCount(run) == 1);
        using (var stop = await Api.StopAsync(client, id))
            Assert.Equal(HttpStatusCode.OK, stop.StatusCode);

        var run = await WaitForAnalysisAsync(client, id, "not_found");
        Assert.Equal("aborted", Api.Status(run));
        var analysis = run.GetProperty("analysis");
        Assert.Equal(1, analysis.GetProperty("analyzed_steps").GetInt32());
        Assert.Equal("Прогон остановлен, анализ только по завершённым ступеням. Отказа не было: предел не ниже 100 запр/с",
            Message(analysis));
    }

    [Fact]
    public async Task Aborted_run_without_steps_is_not_analyzed()
    {
        await using var factory = new CoordinatorFactory();
        var (client, id) = await StartRunAsync(factory, 100);

        using (var stop = await Api.StopAsync(client, id))
            Assert.Equal(HttpStatusCode.OK, stop.StatusCode);

        var run = await Api.WaitForStatusAsync(client, id, "aborted");
        Assert.Equal(JsonValueKind.Null, run.GetProperty("analysis").ValueKind);
        Assert.Empty(factory.Analyzer.Calls);
    }

    [Fact]
    public async Task Steps_from_first_skipped_one_are_not_sent_to_analyzer()
    {
        await using var factory = new CoordinatorFactory();
        var (client, id) = await StartRunAsync(factory, 100, 150, 200, 250);

        factory.Agent.SendStep(TestData.Step(1, 100));
        factory.Agent.SendStep(TestData.Step(2, 150, 6750));
        factory.Agent.SendStep(TestData.Step(3, 200, 8000, skippedCount: 1000));
        factory.Agent.SendStep(TestData.Step(4, 250, 11250));
        factory.Agent.Finish();

        var run = await WaitForAnalysisAsync(client, id, "not_found");
        Assert.Equal(4, Api.StepCount(run));
        var analysis = run.GetProperty("analysis");
        Assert.Equal(2, analysis.GetProperty("analyzed_steps").GetInt32());
        Assert.Equal(150.0, analysis.GetProperty("limit_rps").GetDouble());
        Assert.Equal(
            "Отказа не было до ступени 3: предел не ниже 150 запр/с, дальше агент не успевал подавать нагрузку",
            Message(analysis));

        var sent = Assert.Single(factory.Analyzer.Calls);
        Assert.Equal(new uint[] { 1, 2 }, sent.Select(step => step.Index));
    }

    [Fact]
    public async Task Failure_before_skipped_steps_is_a_real_limit()
    {
        await using var factory = new CoordinatorFactory();
        var (client, id) = await StartRunAsync(factory, 100, 150, 200);
        factory.Analyzer.Respond = _ => Task.FromResult(new LimitResult(true, 100, 2, ["ошибок 5.0 % при пороге 1 %"]));

        factory.Agent.SendStep(TestData.Step(1, 100));
        factory.Agent.SendStep(TestData.Step(2, 150, 6750, errorCount: 340));
        factory.Agent.SendStep(TestData.Step(3, 200, 8000, skippedCount: 1000));
        factory.Agent.Finish();

        var analysis = (await WaitForAnalysisAsync(client, id, "found")).GetProperty("analysis");
        Assert.Equal(100.0, analysis.GetProperty("limit_rps").GetDouble());
        Assert.Equal("Предел: 100 запр/с, отказ на ступени 2 (150 запр/с)", Message(analysis));
    }

    [Fact]
    public async Task Skipped_requests_on_first_step_are_undetermined_without_analyzer()
    {
        await using var factory = new CoordinatorFactory();
        var (client, id) = await StartRunAsync(factory, 100, 150);

        factory.Agent.SendStep(TestData.Step(1, 100, skippedCount: 500));
        factory.Agent.SendStep(TestData.Step(2, 150, 6750));
        factory.Agent.Finish();

        var analysis = (await WaitForAnalysisAsync(client, id, "undetermined")).GetProperty("analysis");
        Assert.Equal(0, analysis.GetProperty("analyzed_steps").GetInt32());
        Assert.Contains("уже на ступени 1", Message(analysis));
        Assert.Empty(factory.Analyzer.Calls);
    }
}
